using CSCore;
using CSCore.Codecs.WAV;
using CSCore.CoreAudioAPI;
using CSCore.SoundIn;
using CSCore.Streams;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace text2speach.net
{
    class Program
    {
        static void Main(string[] args)
        {
            Program.writeSpeakersToWav(args);

        }

        enum CaptureMode
        {
            Capture = 1,
            // ReSharper disable once UnusedMember.Local
            LoopbackCapture = 2
        }
        static void writeSpeakersToWav(string[] args)
        {
            const int GOOGLE_RATE = 16000;
            const int GOOGLE_BITS_PER_SAMPLE = 16;
            const int GOOGLE_CHANNELS = 1;
            const int EARPHONES = 5;

            CaptureMode captureMode = CaptureMode.LoopbackCapture;

            DataFlow dataFlow = captureMode == CaptureMode.Capture ? DataFlow.Capture : DataFlow.Render;

            var devices = MMDeviceEnumerator.EnumerateDevices(dataFlow, DeviceState.Active);
            foreach(var d in devices)
            {
                Console.WriteLine("- {0:#00}: {1}", d, d.FriendlyName);
            }
            var headphones = devices.First(x => x.FriendlyName.StartsWith("small"));

            //using (WasapiCapture capture = new WasapiLoopbackCapture())
            using (WasapiCapture soundIn = captureMode == CaptureMode.Capture
                ? new WasapiCapture()
                : new WasapiLoopbackCapture())
            {
                //if nessesary, you can choose a device here
                //to do so, simply set the device property of the capture to any MMDevice
                //to choose a device, take a look at the sample here: http://cscore.codeplex.com/

                soundIn.Device = headphones;

                Console.WriteLine("Waiting, press any key to start");
                Console.ReadKey();
                //initialize the selected device for recording
                soundIn.Initialize();

                //create a SoundSource around the the soundIn instance
                //this SoundSource will provide data, captured by the soundIn instance
                SoundInSource soundInSource = new SoundInSource(soundIn) { FillWithZeros = false };

                //create a source, that converts the data provided by the
                //soundInSource to any other format
                //in this case the "Fluent"-extension methods are being used
                IWaveSource convertedSource = soundInSource
                    .ChangeSampleRate(GOOGLE_RATE) // sample rate
                    .ToSampleSource()
                    .ToWaveSource(GOOGLE_BITS_PER_SAMPLE); //bits per sample

                var channels = GOOGLE_CHANNELS;
                    
                    //channels...
                using (convertedSource = channels == 1 ? convertedSource.ToMono() : convertedSource.ToStereo())
                //create a wavewriter to write the data to
                using (WaveWriter w = new WaveWriter("dump.wav", convertedSource.WaveFormat))
                {
                    //setup an eventhandler to receive the recorded data
                    //register an event handler for the DataAvailable event of 
                    //the soundInSource
                    //Important: use the DataAvailable of the SoundInSource
                    //If you use the DataAvailable event of the ISoundIn itself
                    //the data recorded by that event might won't be available at the
                    //soundInSource yet
                    soundInSource.DataAvailable += (s, e) =>
                    {
                        //read data from the converedSource
                        //important: don't use the e.Data here
                        //the e.Data contains the raw data provided by the 
                        //soundInSource which won't have your target format
                        byte[] buffer = new byte[convertedSource.WaveFormat.BytesPerSecond / 2];
                        int read;

                        //keep reading as long as we still get some data
                        //if you're using such a loop, make sure that soundInSource.FillWithZeros is set to false
                        while ((read = convertedSource.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            //write the read data to a file
                            // ReSharper disable once AccessToDisposedClosure
                            w.Write(buffer, 0, read);
                        }
                    };


                    //start recording
                    soundIn.Start();

                    Console.WriteLine("Started, press any key to stop");
                    Console.ReadKey();

                    //stop recording
                    soundIn.Stop();
                }
            }
        }
    }
}
