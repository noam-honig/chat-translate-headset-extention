# Sample code recording headphones and converting using google


## To run - creating a wav file
Set startup project to text2speach.net
set startup object to test2speach.net.main

Change the filter in Program.cs line 45 
``` var headphones = devices.First(x => x.FriendlyName.StartsWith("small")); ```

to match how your headphone device
the program will output all the devices when it starts

when started, you'll see a prompt to press any key
start your audio
ensure your headphone are default for audio
press any key

press another key to stop
the dump.wav is created in the debug output folder

## To run - parsing the wav file (text2speach1)
Build text2speach1 - it has a prebuild step to copy the dump.wav from text2speach.net 
Change the credentials path in program.cs
run the project

You will see the transcription - althuogh it seemed to take a while when i ran it
