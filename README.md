# MCAConverter
This tool is for usage in the command line. Remember to cd to the folder containing the MCAConverter.exe file.

### Usage:
MCAConverter.exe \<mode\> \<path\> [version=4] [loopStart=0] [loopEnd=0]

\<mode\> can be<br>
-d for decoding a mca to wav<br>
-e for encoding a wav to mca<br>

#### *Important!*
If you are running into issues encoding, it only supports 16bit PCM for converting wav into mca!

The optional parameters marked with "[]" are only used for the encoding process and have default value.<br>
[version=4] is by default 4 and represents the version the mca should be after encoding<br>

[loopStart=0] is by default 0 and represents at which sample a loop of the encoded track should begin<br>

[loopEnd=0] is by default 0 and represents at which sample a loop of the encoded track should end<br>
