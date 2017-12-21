using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MCAConverter.IO;
using System.IO;

namespace MCAConverter
{
    class Program
    {
        #region Functions
        static sbyte[] NibbleToSByte = { 0, 1, 2, 3, 4, 5, 6, 7, -8, -7, -6, -5, -4, -3, -2, -1 };

        static sbyte GetLowNibble(byte value)
        {
            return NibbleToSByte[value & 0xF];
        }

        static sbyte GetHighNibble(byte value)
        {
            return NibbleToSByte[value >> 4];
        }

        static short Clamp(int value)
        {
            if (value < -32768) value = -32768;
            if (value > 32767) value = 32767;
            return (short)value;
        }
        #endregion

        #region Classes/Structs
        public class Channel
        {
            public List<short> adpcmCoefs = new List<short>();
            public short hist1 = 0;
            public short hist2 = 0;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public class Header
        {
            public Magic magic = "MADP";
            public short version;
            public short zero0 = 0;
            public short channelCount;
            public short interleaveBlockSize = 0x100;
            public int numSamples;
            public int sampleRate;
            public int loopStart;
            public int loopEnd;
            public int headSize;
            public int dataSize;
            public float unk4;
            public short coefShift = 0x0;
            public short unk5;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public class WAVHeader
        {
            public Magic RIFFMagic = "RIFF";
            public int fileSize;
            public Magic RIFFType = "WAVE";

            public Magic fmtTag = "fmt ";
            public int chunkSize = 0x10;
            public short formatTag = 0x1;
            public short channelCount;
            public int sampleRate;
            public int avgBytesPerSec;
            public short blockAlign = 0x2;
            public short bitsPerSample = 0x10;
        }
        #endregion

        static void Main(string[] args)
        {
            if (args.Count() < 2)
            {
                Console.WriteLine("Usage: MCAConverter.exe <mode> <path> [version=4] [loopstart=0] [loopend=0]\nThe optional parameters are only used by mode -e and have a default value.");
                Environment.Exit(0);
            }

            if (args[0] != "-d" && args[0] != "-e")
            {
                Console.WriteLine($"Unknown mode \"{args[0]}\".\n\nSupported modes:\n-d\tDecode a mca to wav\n-e\tEncode a wav to mca");
                Environment.Exit(0);
            }

            if (!File.Exists(args[1]))
            {
                Console.WriteLine($"Couldn't open file {args[1]}.");
                Environment.Exit(0);
            }

            var version = 4;
            if (args.Count() > 2)
            {
                if (!Int32.TryParse(args[2], out version))
                {
                    throw new Exception("Version isn't a valid number!");
                }
                else
                {
                    if (version < 0)
                    {
                        throw new Exception("Version can't be negative!");
                    }
                }
            }

            if (args[0] == "-d")
            {
                var wav = DecodeMCAtoWAV(args[1]);
                File.WriteAllBytes(args[1] + ".wav", wav);
            }
            else if (args[0] == "-e")
            {
                var loopStart = 0;
                var loopEnd = 0;
                if (args.Count() > 3)
                {
                    if (!Int32.TryParse(args[3], out loopStart))
                    {
                        loopStart = 0;
                    }
                    else
                    {
                        if (loopStart < 0) loopStart = 0;
                    }
                }
                if (args.Count() > 4)
                {
                    if (!Int32.TryParse(args[4], out loopStart))
                    {
                        loopEnd = 0;
                    }
                    else
                    {
                        if (loopEnd < 0) loopEnd = 0;
                    }
                }

                var mca = EncodeWAVtoMCA(args[1], version, loopStart, loopEnd);
                File.WriteAllBytes(args[1] + ".mca", mca);
            }

        }

        public static byte[] DecodeMCAtoWAV(string mcaFile)
        {
            using (var br = new BinaryReaderX(File.OpenRead(mcaFile)))
            {
                //Header
                var header = br.ReadStruct<Header>();

                //check info
                if (header.magic != "MADP")
                {
                    Console.WriteLine("This is no mca file.");
                    Environment.Exit(0);
                }
                if (header.channelCount != 1)
                {
                    Console.WriteLine("Only mca's with one channel are supported.");
                    Environment.Exit(0);
                }

                //Version specifics
                int headSize, coefShift, coefStart, startOffset, coefOffset;
                int coefSpacing = 0x30;
                if (header.version <= 3)
                {
                    headSize = (int)br.BaseStream.Length - header.dataSize;
                    coefShift = 0x0;
                    coefStart = headSize - coefSpacing * header.channelCount;

                    startOffset = headSize;
                    coefOffset = coefStart + coefShift * 0x14;
                }
                else if (header.version == 4)
                {
                    headSize = header.headSize;
                    coefShift = header.coefShift;
                    coefStart = headSize - coefSpacing * header.channelCount;

                    startOffset = (int)br.BaseStream.Length - header.dataSize;
                    coefOffset = coefStart + coefShift * 0x14;
                }
                else
                {
                    headSize = header.headSize;
                    coefShift = header.coefShift;
                    coefStart = headSize - coefSpacing * header.channelCount;

                    var tmpOff = br.BaseStream.Position;
                    br.BaseStream.Position = coefStart - 0x4;
                    startOffset = br.ReadInt32();
                    coefOffset = coefStart + coefShift * 0x14;
                    br.BaseStream.Position = tmpOff;
                }

                //sanity check (for bad rips with the header manually truncated to in attempt to "fix" v5 headers)
                var fileSize = br.BaseStream.Length;

                if (startOffset + header.dataSize > fileSize)
                {
                    if (headSize + header.dataSize > fileSize)
                        throw new Exception("Mismatching information. headSize + dataSize don't correlate with fileSize.\nPlease check the header.");

                    startOffset = (int)fileSize - header.dataSize;
                }

                //Setup coefs
                var channels = SetupCoefs(br.BaseStream, coefOffset, header.channelCount, coefSpacing);

                //Decode NGC_DSP
                br.BaseStream.Position = startOffset;
                var decode = DecodeNGCDSP(br.ReadBytes(header.dataSize), header, channels);

                //Create WAV
                var wavHeader = new WAVHeader
                {
                    fileSize = decode.Length + 0x28 - 0x8,
                    channelCount = header.channelCount,
                    sampleRate = header.sampleRate,
                    avgBytesPerSec = header.sampleRate * 0x2
                };
                var wavStream = new MemoryStream();
                using (var bw = new BinaryWriterX(wavStream, true))
                {
                    bw.WriteStruct(wavHeader);
                    bw.WriteASCII("data");
                    bw.Write(decode.Length);
                    bw.Write(decode);
                }

                return wavStream.ToArray();
            }
        }

        public static byte[] EncodeWAVtoMCA(string wavFile, int version, int loopStart, int loopEnd)
        {
            using (var br = new BinaryReaderX(File.OpenRead(wavFile)))
            {
                //Header
                var wavHeader = br.ReadStruct<WAVHeader>();

                //check info
                if (wavHeader.RIFFMagic != "RIFF" || wavHeader.RIFFType != "WAVE")
                {
                    throw new Exception("Unsupported WAV.");
                }
                if (wavHeader.channelCount != 1)
                {
                    throw new Exception("More than one channel isn't supported.");
                }
                if (wavHeader.formatTag != 1)
                {
                    throw new Exception("Only 16bit PCM supported.");
                }
                if (wavHeader.bitsPerSample != 16)
                {
                    throw new Exception("Only 16bit PCM supported.");
                }
                var numSamples = (((int)br.BaseStream.Length - 0x2c) * 8) / wavHeader.bitsPerSample;
                if (loopStart > numSamples)
                    loopStart = numSamples;
                if (loopEnd < loopStart)
                    loopEnd = loopStart;
                if (loopEnd > numSamples)
                    loopEnd = numSamples;

                //Encode NGCDSP
                br.BaseStream.Position += 4;
                var soundDataSize = br.ReadInt32();
                var soundData = br.ReadBytes(soundDataSize);

                //Create coefs
                var coefs = GetCoefs(soundData, numSamples);

                //Encode
                var encode = EncodeNGCDSP(soundData, numSamples, coefs);

                //Create MCA
                var mcaHeader = new Header
                {
                    version = (short)version,
                    channelCount = wavHeader.channelCount,
                    numSamples = numSamples,
                    sampleRate = wavHeader.sampleRate,
                    loopStart = loopStart,
                    loopEnd = loopEnd,
                    headSize = 0x34 + 0x30 * wavHeader.channelCount,
                    dataSize = encode.Length,
                    unk4 = 0,
                    unk5 = 0
                };
                var ms = new MemoryStream();
                using (var bw = new BinaryWriterX(ms, true))
                {
                    bw.WriteStruct(mcaHeader);
                    bw.Write(0);
                    bw.Write(0);

                    for (var i = 0; i < 8; i++)
                        for (var j = 0; j < 2; j++)
                            bw.Write(coefs[i][j]);
                    bw.WritePadding(0x10);

                    bw.Write(encode);
                }

                return ms.ToArray();
            }
        }

        public static List<Channel> SetupCoefs(Stream input, int coefOffset, int channelCount, int coefSpacing)
        {
            var startOffset = input.Position;

            var channels = new List<Channel>();

            using (var br = new BinaryReaderX(input, true))
            {
                br.BaseStream.Position = coefOffset;
                using (var coefBr = new BinaryReaderX(new MemoryStream(br.ReadBytes(coefSpacing))))
                {
                    for (int ch = 0; ch < channelCount; ch++)
                    {
                        channels.Add(new Channel());
                        for (int i = 0; i < 16; i++)
                        {
                            channels[ch].adpcmCoefs.Add(coefBr.ReadInt16());
                        }
                        br.BaseStream.Position = coefOffset + (ch + 1) * coefSpacing;
                    }
                }
            }

            input.Position = startOffset;

            return channels;
        }

        static byte[] DecodeNGCDSP(byte[] soundData, Header header, List<Channel> channels)
        {
            var ms = new MemoryStream();

            using (var outputBw = new BinaryWriterX(ms, true))
            using (var soundBr = new BinaryReaderX(new MemoryStream(soundData)))
            {
                short hist1 = 0;// header.initHist1;
                short hist2 = 0; //header.initHist2;

                while (soundBr.BaseStream.Position < soundBr.BaseStream.Length)
                {
                    for (int chanNum = 0; chanNum < header.channelCount; chanNum++)
                    {
                        var block = soundBr.ReadBytes(header.interleaveBlockSize);
                        using (var blockBr = new BinaryReaderX(new MemoryStream(block)))
                        {
                            while (blockBr.BaseStream.Position < blockBr.BaseStream.Length)
                            {
                                // Each frame, we need to read the header byte and use it to set the scale and coefficient values:
                                byte head = blockBr.ReadByte();

                                ushort scale = (ushort)(1 << (head & 0xF));
                                byte coefIndex = (byte)(head >> 4);
                                short coef1 = channels[chanNum].adpcmCoefs[2 * coefIndex];
                                short coef2 = channels[chanNum].adpcmCoefs[2 * coefIndex + 1];

                                // 7 bytes per frame
                                for (uint i = 0; i < 7; i++)
                                {
                                    byte b = blockBr.ReadByte();

                                    // 2 samples per byte
                                    for (uint s = 0; s < 2; s++)
                                    {
                                        sbyte adpcmNibble = (s == 0) ? GetHighNibble(b) : GetLowNibble(b);
                                        short sample = Clamp(((adpcmNibble * scale) << 11) + 1024 + ((coef1 * hist1) + (coef2 * hist2)) >> 11);

                                        hist2 = hist1;
                                        hist1 = sample;
                                        outputBw.Write(sample);
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return ms.ToArray();
        }

        static short[][] GetCoefs(byte[] soundData, int numSamples)
        {
            return NGCDSPEncoder.DSPCorrelateCoefs(soundData, numSamples);
        }

        static byte[] EncodeNGCDSP(byte[] soundData, int numSamples, short[][] coefs)
        {
            //Create ADPCM Data by frame
            var ms = new MemoryStream();
            using (var bw = new BinaryWriterX(ms, true))
            using (var br = new BinaryReaderX(new MemoryStream(soundData)))
            {
                List<short> pcmBlock = null;
                while (br.BaseStream.Position < br.BaseStream.Length)
                {
                    //Set history values
                    if (pcmBlock != null)
                    {
                        var y = pcmBlock[14];
                        var n = pcmBlock[15];
                        pcmBlock = new List<short>();
                        pcmBlock.Add(y);
                        pcmBlock.Add(n);
                    }
                    else
                    {
                        pcmBlock = new List<short>();
                        pcmBlock.Add(0);
                        pcmBlock.Add(0);
                    }

                    //Get PCMBlock for frame
                    if (br.BaseStream.Length - br.BaseStream.Position < 28)
                    {
                        for (int i = 0; i < br.BaseStream.Length - br.BaseStream.Position; i += 2)
                            pcmBlock.Add(br.ReadInt16());
                        while (pcmBlock.Count() < 14) pcmBlock.Add(0);
                    }
                    else
                    {
                        for (int i = 0; i < 14; i++)
                            pcmBlock.Add(br.ReadInt16());
                    }

                    //Convert PCMBlock to ADPCM frame
                    var adpcm = NGCDSPEncoder.DSPEncodeFrame(pcmBlock.ToArray(), 14, coefs);
                    bw.Write(adpcm);
                }
            }

            return ms.ToArray();
        }
    }
}
