
/*
 * Copyright (c) 2019 Google Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License"); you may not
 * use this file except in compliance with the License. You may obtain a copy of
 * the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS, WITHOUT
 * WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the
 * License for the specific language governing permissions and limitations under
 * the License.
 */

using Google.Cloud.Speech.V1;
using Google.Protobuf;

using NAudio.Wave;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CSCore;
using CSCore.CoreAudioAPI;
using CSCore.SoundIn;
using CSCore.Streams;
using WasapiLoopbackCapture = CSCore.SoundIn.WasapiLoopbackCapture;


namespace GoogleCloudSamples
{
    class Program
    {
        const string apikey = @"D:\code\firefly\fireflyrecognize-googleapikey_11a3398d323c.json";
        static void Main(string[] args)
        {
     //       Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", apikey);
            var ct = new CancellationTokenSource();


            //using (var i = InfiniteStreaming.ListenToMyMic())
            //{
            //    Console.WriteLine("Listening, say exit to stop");
            //    i.RecognizeAsync(ct.Token).Wait();

            //    ct.Cancel();


            //}
            using (var i = InfiniteStreaming.ListenToWhatsPlayingOnMyHeadset())
            {
                Console.WriteLine("Listening, say exit to stop");
                i.RecognizeAsync(ct.Token).Wait();

                ct.Cancel();


            }
        }

    }
    
    /// <summary>
    /// Sample code for infinite streaming. The strategy for infinite streaming is to restart each stream
    /// shortly before it would time out (currently at 5 minutes). We keep track of the end result time of
    /// of when we last saw a "final" transcription, and resend the audio data we'd recorded past that point.
    /// </summary>
    public class InfiniteStreaming : IDisposable
    {
        private const int SampleRate = 16000;
        private const int ChannelCount = 1;
        private const int BytesPerSample = 2;
        private const int BytesPerSecond = SampleRate * ChannelCount * BytesPerSample;
        private static readonly TimeSpan s_streamTimeLimit = TimeSpan.FromSeconds(290);

        private readonly SpeechClient _client;

        /// <summary>
        /// Microphone chunks that haven't yet been processed at all.
        /// </summary>
        private readonly BlockingCollection<ByteString> _microphoneBuffer = new BlockingCollection<ByteString>();

        /// <summary>
        /// Chunks that have been sent to Cloud Speech, but not yet finalized.
        /// </summary>
        private readonly LinkedList<ByteString> _processingBuffer = new LinkedList<ByteString>();

        /// <summary>
        /// The start time of the processing buffer, in relation to the start of the stream.
        /// </summary>
        private TimeSpan _processingBufferStart;

        /// <summary>
        /// The current RPC stream, if any.
        /// </summary>
        private SpeechClient.StreamingRecognizeStream _rpcStream;

        /// <summary>
        /// The deadline for when we should stop the current stream.
        /// </summary>
        private DateTime _rpcStreamDeadline;

        /// <summary>
        /// The task indicating when the next response is ready, or when we've
        /// reached the end of the stream. (The task will complete in either case, with a result
        /// of True if it's moved to another response, or False at the end of the stream.)
        /// </summary>
        private ValueTask<bool> _serverResponseAvailableTask;

        private WasapiCapture _soundIn;
        private SoundInSource _soundInSource;
        private IWaveSource _convertedSource;
        private WasapiCapture _headphones;
        private CaptureMode _captureMode;

        public static InfiniteStreaming ListenToMyMic()
        {
            return new InfiniteStreaming(CaptureMode.Capture);
        }
        public static InfiniteStreaming ListenToWhatsPlayingOnMyHeadset()
        {
            return new InfiniteStreaming(CaptureMode.LoopbackCapture);
        }
        private InfiniteStreaming(CaptureMode captureMode)
        {
            _client = SpeechClient.Create();
            _captureMode = captureMode;
        }

        /// <summary>
        /// Runs the main loop until "exit" or "quit" is heard.
        /// </summary>
        /// <param name="cancellationToken"></param>
        private async Task RunAsync(CancellationToken cancellationToken)
        {
            StartListeningOnLoopback();


            Console.WriteLine("Listening");
            _soundIn.Start();
            _soundIn.Stopped += (sender, args) =>
            {
                Console.WriteLine("Mic stopped");
            };

        

            while (true && !cancellationToken.IsCancellationRequested)
            {
                await MaybeStartStreamAsync();
                // ProcessResponses will return false if it hears "exit" or "quit".
                if (!ProcessResponses())
                {
                    Console.WriteLine("User Exited");
                    
                    return;
                }
                await TransferMicrophoneChunkAsync();
            }
           

        }

        /// <summary>
        /// Starts a new RPC streaming call if necessary. This will be if either it's the first call
        /// (so we don't have a current request) or if the current request will time out soon.
        /// In the latter case, after starting the new request, we copy any chunks we'd already sent
        /// in the previous request which hadn't been included in a "final result".
        /// </summary>
        private async Task MaybeStartStreamAsync()
        {
            var now = DateTime.UtcNow;
            if (_rpcStream != null && now >= _rpcStreamDeadline)
            {
                Console.WriteLine($"Closing stream before it times out");
                await _rpcStream.WriteCompleteAsync();
                _rpcStream.GrpcCall.Dispose();
                _rpcStream = null;
            }

            // If we have a valid stream at this point, we're fine.
            if (_rpcStream != null)
            {
                //Console.WriteLine("We already have a google stream");
                return;
            }
            Console.WriteLine("Creating new google stream");
            // We need to create a new stream, either because we're just starting or because we've just closed the previous one.
            _rpcStream = _client.StreamingRecognize();
            _rpcStreamDeadline = now + s_streamTimeLimit;
            _processingBufferStart = TimeSpan.Zero;
            _serverResponseAvailableTask = _rpcStream.GetResponseStream().MoveNextAsync();
            await _rpcStream.WriteAsync(new StreamingRecognizeRequest
            {
                StreamingConfig = new StreamingRecognitionConfig
                {
                    Config = new RecognitionConfig
                    {
                        Encoding = RecognitionConfig.Types.AudioEncoding.Linear16,
                        SampleRateHertz = SampleRate,
                        LanguageCode = "en-US",
                        MaxAlternatives = 1,
                        UseEnhanced=true,
                        


                    },
                    InterimResults = true,
                    //SingleUtterance=false
                }
            });

            Console.WriteLine($"Writing {_processingBuffer.Count} chunks into the new stream.");
            foreach (var chunk in _processingBuffer)
            {
                await WriteAudioChunk(chunk);
            }
        }

        /// <summary>
        /// Processes responses received so far from the server,
        /// returning whether "exit" or "quit" have been heard.
        /// </summary>
        private bool ProcessResponses()
        {
            while (_serverResponseAvailableTask.IsCompleted && _serverResponseAvailableTask.Result)
            {
                var response = _rpcStream.GetResponseStream().Current;
                _serverResponseAvailableTask = _rpcStream.GetResponseStream().MoveNextAsync();
                // Uncomment this to see the details of interim results.
                // Console.WriteLine($"Response: {response}");

                // See if one of the results is a "final result". If so, we trim our
                // processing buffer.
                var finalResult = response.Results.FirstOrDefault(r => true);
                if (finalResult != null)
                {
                    string transcript = finalResult.Alternatives[0].Transcript;
                    Console.WriteLine($"Transcript: {transcript}");
                    if (transcript.ToLowerInvariant().Contains("exit") ||
                        transcript.ToLowerInvariant().Contains("quit"))
                    {
                        return false;
                    }

                    TimeSpan resultEndTime = finalResult.ResultEndTime.ToTimeSpan();

                    // Rather than explicitly iterate over the list, we just always deal with the first
                    // element, either removing it or stopping.
                    int removed = 0;
                    while (_processingBuffer.First != null)
                    {
                        var sampleDuration = TimeSpan.FromSeconds(_processingBuffer.First.Value.Length / (double)BytesPerSecond);
                        var sampleEnd = _processingBufferStart + sampleDuration;

                        // If the first sample in the buffer ends after the result ended, stop.
                        // Note that part of the sample might have been included in the result, but the samples
                        // are short enough that this shouldn't cause problems.
                        if (sampleEnd > resultEndTime)
                        {
                            break;
                        }
                        _processingBufferStart = sampleEnd;
                        _processingBuffer.RemoveFirst();
                        removed++;
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// Takes a single sample chunk from the microphone buffer, keeps a local copy
        /// (in case we need to send it again in a new request) and sends it to the server.
        /// </summary>
        /// <returns></returns>
        private async Task TransferMicrophoneChunkAsync()
        {
            // This will block - but only for ~100ms, unless something's really broken.
            var chunk = _microphoneBuffer.Take();
            //Console.WriteLine(("ProcessingBuffer AddLast" + chunk.Length.ToString()));
            _processingBuffer.AddLast(chunk);
            await WriteAudioChunk(chunk);
        }

        /// <summary>
        /// Writes a single chunk to the RPC stream.
        /// </summary>
        private Task WriteAudioChunk(ByteString chunk) =>
            _rpcStream.WriteAsync(new StreamingRecognizeRequest { AudioContent = chunk });

        enum CaptureMode
        {
            Capture = 1,
            // ReSharper disable once UnusedMember.Local
            LoopbackCapture = 2
        }
        private WasapiCapture StartListeningOnLoopback()
        {
            const int GOOGLE_RATE = 16000;
            const int GOOGLE_BITS_PER_SAMPLE = 16;
            const int GOOGLE_CHANNELS = 1;

            CaptureMode captureMode = _captureMode;

            DataFlow dataFlow = captureMode == CaptureMode.Capture ? DataFlow.Capture : DataFlow.Render;

            var devices = MMDeviceEnumerator.EnumerateDevices(dataFlow, DeviceState.Active);
            Console.WriteLine("Please select devlice:");
            for (int i = 0; i < devices.Count; i++)
            {
                Console.WriteLine(i + ") " + devices[i].FriendlyName);
            }
            var deviceIndex= int.Parse(Console.ReadLine());
            
            var headphones = devices[deviceIndex];

            //using (WasapiCapture capture = new WasapiLoopbackCapture())
            _soundIn = captureMode == CaptureMode.Capture
                ? new WasapiCapture()
                : new WasapiLoopbackCapture();

            //if nessesary, you can choose a device here
            //to do so, simply set the device property of the capture to any MMDevice
            //to choose a device, take a look at the sample here: http://cscore.codeplex.com/

            _soundIn.Device = headphones;


            //initialize the selected device for recording
            _soundIn.Initialize();
            //create a SoundSource around the the soundIn instance
            //this SoundSource will provide data, captured by the soundIn instance
            _soundInSource = new SoundInSource(_soundIn) { FillWithZeros = false };

            //create a source, that converts the data provided by the
            //soundInSource to any other format
            //in this case the "Fluent"-extension methods are being used
            _convertedSource = _soundInSource
                .ChangeSampleRate(GOOGLE_RATE) // sample rate
                .ToSampleSource()
                .ToWaveSource(GOOGLE_BITS_PER_SAMPLE); //bits per sample

            var channels = GOOGLE_CHANNELS;

            //channels...
            var src = channels == 1 ? _convertedSource.ToMono() : _convertedSource.ToStereo();


            _soundInSource.DataAvailable += (sender, args) =>
            {
                //read data from the converedSource
                //important: don't use the e.Data here
                //the e.Data contains the raw data provided by the 
                //soundInSource which won't have your target format
                byte[] buffer = new byte[_convertedSource.WaveFormat.BytesPerSecond / 2];
                int read;

                //keep reading as long as we still get some data
                //if you're using such a loop, make sure that soundInSource.FillWithZeros is set to false
                while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    //write the read data to a file
                    // ReSharper disable once AccessToDisposedClosure
                    Debug.WriteLine($"Read {read} bytes");
                    _microphoneBuffer.Add(ByteString.CopyFrom(buffer, 0, read));

                    //w.Write(buffer, 0, read);
                }
                
            };



            return _soundIn;

        }

        /// <summary>
        /// Note: the calling code expects a Task with a return value for consistency with
        /// other samples.
        /// We'll always return a task which eventually faults or returns 0.
        /// </summary>
        public async Task<int> RecognizeAsync(CancellationToken ct)
        {

            await RunAsync(ct);
            return 0;
        }


        public void Dispose()
        {
            _microphoneBuffer?.Dispose();
            _soundIn?.Dispose();
            _soundInSource?.Dispose();
            _convertedSource?.Dispose();
        }
    }
}