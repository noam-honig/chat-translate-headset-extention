using GoogleCloudSamples;

using System;

namespace text2speach1
{
    class Program
    {
        const string apikey = @"D:\code\firefly\fireflyrecognize-googleapikey_11a3398d323c.json";

        static void Main(string[] args)
        {

            Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", apikey);

             Recognize.doMain(args);

            //InfiniteStreaming.RecognizeAsync().Wait();


        }
    }


}

