using System;
using System.Collections.Generic;
using System.IO;

namespace SteganoWave
{
    /// <summary>
    /// Hides/extracts data in/from a wave stream using FFT phase encoding.
    ///
    /// This is the technique from the lecture slide
    /// "Sound Steganography in Uncompressed Audio Using Phase Encoding":
    ///
    ///   Embedding : WAV -> FFT -> select frequency bins (via key)
    ///               -> encode each bit as +d phi (bit 1) / -d phi (bit 0)
    ///               -> Inverse FFT -> stego WAV
    ///   Extraction: stego WAV -> FFT -> read phase of the same bins
    ///               -> detect the sign of the phase -> reconstruct bits.
    ///
    /// Only the phase of the selected bins is changed, the magnitude is left
    /// untouched, so the carrier wave keeps its spectral envelope.
    /// </summary>
    public class WaveUtility
    {
        /// <summary>
        /// Number of samples that make up one FFT block. Must be a power of two
        /// (the radix-2 FFT below relies on that).
        /// </summary>
        private const int BlockSize = 1024;

        /// <summary>
        /// Highest frequency bin that may carry data. We stay in the lower part
        /// of the spectrum [1 .. MaxBin], where real speech/music has enough
        /// energy for the phase to survive PCM quantisation. DC (bin 0) and the
        /// Nyquist bin (BlockSize/2) are never used.
        /// </summary>
        private const int MaxBin = BlockSize / 4; // 256

        /// <summary>How many bits are stored in a single FFT block.</summary>
        private const int BitsPerBlock = 32;

        /// <summary>
        /// The phase that encodes a bit: +PhaseShift means "1", -PhaseShift
        /// means "0". The slide suggests very small shifts (e.g. +-0.1 rad) for
        /// inaudibility. Because we extract blindly (without the original wave)
        /// from a re-quantised 8/16-bit PCM signal, we set the phase to +-pi/2,
        /// which puts the decision boundary (phase = 0) as far as possible from
        /// both states and makes the recovered bit robust to rounding noise.
        /// Lower this value if imperceptibility matters more than robustness.
        /// </summary>
        private const double PhaseShift = Math.PI / 2;

        /// <summary>
        /// The read-only stream.
        /// Clean wave for hiding,
        /// Carrier wave for extracting
        /// </summary>
        private WaveStream sourceStream;

        /// <summary>Stream to receive the edited carrier wave</summary>
        private Stream destinationStream;

        /// <summary>bits per sample / 8</summary>
        private int bytesPerSample;

        /// <summary>Initializes a new WaveUtility for hiding a message</summary>
        /// <param name="sourceStream">Clean wave</param>
        /// <param name="destinationStream">
        /// Header of the clean wave
        /// This stream will receive the complete carrier wave
        /// </param>
        public WaveUtility(WaveStream sourceStream, Stream destinationStream)
            : this(sourceStream)
        {
            this.destinationStream = destinationStream;
        }

        /// <summary>Initializes a new WaveUtility for extracting a message</summary>
        /// <param name="sourceStream">Carrier wave</param>
        public WaveUtility(WaveStream sourceStream)
        {
            this.sourceStream = sourceStream;
            this.bytesPerSample = sourceStream.Format.wBitsPerSample / 8;

            if (bytesPerSample != 1 && bytesPerSample != 2)
                throw new Exception("Only 8-bit and 16-bit PCM wave files are supported.");
        }

        /// <summary>
        /// Hide [messageStream] in [sourceStream] using phase encoding,
        /// write the resulting carrier wave to [destinationStream].
        /// </summary>
        /// <param name="messageStream">The message to hide (length prefix included)</param>
        /// <param name="keyStream">
        /// A key stream. It is used to choose which frequency bins of every
        /// block carry the message bits.
        /// </param>
        public void Hide(Stream messageStream, Stream keyStream)
        {
            double[] samples = ReadAllSamples();
            int blockCount = samples.Length / BlockSize;

            messageStream.Seek(0, SeekOrigin.Begin);
            List<int> bits = new List<int>();
            int currentByte;
            while ((currentByte = messageStream.ReadByte()) >= 0)
            {
                for (int bitIndex = 0; bitIndex < 8; bitIndex++)
                    bits.Add((currentByte >> bitIndex) & 1);
            }

            double[] re = new double[BlockSize];
            double[] im = new double[BlockSize];

            int bitPos = 0;
            for (int block = 0; block < blockCount && bitPos < bits.Count; block++)
            {
                int offset = block * BlockSize;

                LoadBlock(samples, offset, re, im);
                Fft(re, im, false);

                int[] bins = SelectBins(keyStream, BitsPerBlock);

                for (int i = 0; i < bins.Length && bitPos < bits.Count; i++)
                {
                    EncodeBit(re, im, bins[i], bits[bitPos]);
                    bitPos++;
                }

                Fft(re, im, true);
                StoreBlock(samples, offset, re);
            }

            if (bitPos < bits.Count)
                throw new Exception(
                    "The carrier file is too small for this message and key.\r\n"
                    + "Bits to hide: " + bits.Count + "\r\n"
                    + "Capacity (bits): " + (long)blockCount * BitsPerBlock);

            WriteAllSamples(samples);
        }

        /// <summary>Extract a message from [sourceStream] into [messageStream]</summary>
        /// <param name="messageStream">Empty stream to receive the extracted message</param>
        /// <param name="keyStream">
        /// The same key stream that was used while hiding, it selects the
        /// frequency bins that carry the message bits.
        /// </param>
        public void Extract(Stream messageStream, Stream keyStream)
        {
            double[] samples = ReadAllSamples();
            int blockCount = samples.Length / BlockSize;

            byte[] header = new byte[4];
            long totalBytes = header.Length;
            long byteIndex = 0;
            int currentByte = 0;
            int bitInByte = 0;
            bool done = false;

            double[] re = new double[BlockSize];
            double[] im = new double[BlockSize];

            for (int block = 0; block < blockCount && !done; block++)
            {
                int offset = block * BlockSize;

                LoadBlock(samples, offset, re, im);
                Fft(re, im, false);

                int[] bins = SelectBins(keyStream, BitsPerBlock);

                for (int i = 0; i < bins.Length && !done; i++)
                {
                    int bit = DecodeBit(re, im, bins[i]);
                    currentByte |= (bit << bitInByte);
                    bitInByte++;

                    if (bitInByte < 8)
                        continue;

                    if (byteIndex < header.Length)
                    {
                        header[byteIndex] = (byte)currentByte;

                        if (byteIndex == header.Length - 1)
                        {
                            int messageLength = BitConverter.ToInt32(header, 0);
                            if (messageLength < 0)
                                throw new Exception("Could not extract a message (wrong key?).");

                            totalBytes = header.Length + (long)messageLength;
                        }
                    }
                    else
                    {
                        messageStream.WriteByte((byte)currentByte);
                    }

                    byteIndex++;
                    currentByte = 0;
                    bitInByte = 0;

                    if (byteIndex >= totalBytes)
                        done = true;
                }
            }

            if (!done)
                throw new Exception("Could not extract the complete message (wrong key or corrupt carrier?).");
        }

        /// <summary>
        /// Minimum length (in samples) of a carrier wave that can hold a message
        /// of [messageLength] bytes with the phase-encoding scheme.
        /// </summary>
        /// <param name="keyStream">Key stream (only its position is reset here)</param>
        /// <param name="messageLength">Length of the message in bytes (length prefix included)</param>
        /// <returns>Minimum required count of samples</returns>
        public static long CheckKeyForMessage(Stream keyStream, long messageLength)
        {
            long messageBits = messageLength * 8;
            long requiredBlocks = (messageBits + BitsPerBlock - 1) / BitsPerBlock;

            keyStream.Seek(0, SeekOrigin.Begin);
            return requiredBlocks * BlockSize;
        }

        #region frequency-domain helpers

        /// <summary>Encode one bit into the phase of bin [k], keeping its magnitude.</summary>
        private static void EncodeBit(double[] re, double[] im, int k, int bit)
        {
            double magnitude = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
            double phase = (bit == 1) ? PhaseShift : -PhaseShift;

            re[k] = magnitude * Math.Cos(phase);
            im[k] = magnitude * Math.Sin(phase);

            // keep the spectrum conjugate-symmetric so the IFFT stays real
            int mirror = re.Length - k;
            re[mirror] = re[k];
            im[mirror] = -im[k];
        }

        /// <summary>Read the bit stored in the phase of bin [k] (+phase -> 1).</summary>
        private static int DecodeBit(double[] re, double[] im, int k)
        {
            double phase = Math.Atan2(im[k], re[k]);
            return (phase >= 0) ? 1 : 0;
        }

        /// <summary>
        /// Choose [count] distinct frequency bins (in [1 .. MaxBin]) for the
        /// current block. The walk is driven entirely by the key, so the encoder
        /// and the decoder pick exactly the same bins.
        /// </summary>
        private static int[] SelectBins(Stream keyStream, int count)
        {
            HashSet<int> used = new HashSet<int>();
            int[] bins = new int[count];

            long position = 0;
            for (int i = 0; i < count; i++)
            {
                int step = GetKeyValue(keyStream);
                if (step == 0) step = 1;          // always move forward
                position += step;

                int bin = (int)(((position - 1) % MaxBin) + 1); // 1 .. MaxBin
                while (used.Contains(bin))
                    bin = (bin % MaxBin) + 1;     // linear probe to a free bin

                used.Add(bin);
                bins[i] = bin;
            }
            return bins;
        }

        #endregion

        #region sample <-> stream conversion

        /// <summary>Read the whole data chunk of [sourceStream] as real samples.</summary>
        private double[] ReadAllSamples()
        {
            int dataLength = (int)sourceStream.Length;
            byte[] buffer = new byte[dataLength];

            int read = 0, n;
            while (read < dataLength &&
                   (n = sourceStream.Read(buffer, read, dataLength - read)) > 0)
            {
                read += n;
            }

            int sampleCount = read / bytesPerSample;
            double[] samples = new double[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                if (bytesPerSample == 2)
                {
                    // 16-bit PCM is signed, little-endian
                    short value = (short)(buffer[2 * i] | (buffer[2 * i + 1] << 8));
                    samples[i] = value;
                }
                else
                {
                    // 8-bit PCM is unsigned (0 .. 255)
                    samples[i] = buffer[i];
                }
            }
            return samples;
        }

        /// <summary>Write [samples] back to [destinationStream] as PCM bytes.</summary>
        private void WriteAllSamples(double[] samples)
        {
            byte[] buffer = new byte[samples.Length * bytesPerSample];

            for (int i = 0; i < samples.Length; i++)
            {
                int value = (int)Math.Round(samples[i]);

                if (bytesPerSample == 2)
                {
                    if (value > short.MaxValue) value = short.MaxValue;
                    else if (value < short.MinValue) value = short.MinValue;

                    buffer[2 * i] = (byte)(value & 0xFF);
                    buffer[2 * i + 1] = (byte)((value >> 8) & 0xFF);
                }
                else
                {
                    if (value > 255) value = 255;
                    else if (value < 0) value = 0;

                    buffer[i] = (byte)value;
                }
            }

            destinationStream.Write(buffer, 0, buffer.Length);
        }

        /// <summary>Copy [BlockSize] samples starting at [offset] into the FFT buffers.</summary>
        private static void LoadBlock(double[] samples, int offset, double[] re, double[] im)
        {
            for (int i = 0; i < BlockSize; i++)
            {
                re[i] = samples[offset + i];
                im[i] = 0.0;
            }
        }

        /// <summary>Copy the real part of an IFFT result back into the sample array.</summary>
        private static void StoreBlock(double[] samples, int offset, double[] re)
        {
            for (int i = 0; i < BlockSize; i++)
                samples[offset + i] = re[i];
        }

        #endregion

        #region FFT

        /// <summary>
        /// In-place iterative radix-2 Cooley-Tukey FFT.
        /// [invert] == false: forward transform (time -> frequency).
        /// [invert] == true : inverse transform (frequency -> time), 1/N scaled.
        /// </summary>
        private static void Fft(double[] re, double[] im, bool invert)
        {
            int n = re.Length;

            // bit-reversal permutation
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1)
                    j ^= bit;
                j ^= bit;

                if (i < j)
                {
                    double tr = re[i]; re[i] = re[j]; re[j] = tr;
                    double ti = im[i]; im[i] = im[j]; im[j] = ti;
                }
            }

            // butterflies
            for (int len = 2; len <= n; len <<= 1)
            {
                double angle = 2.0 * Math.PI / len * (invert ? 1.0 : -1.0);
                double wRe = Math.Cos(angle);
                double wIm = Math.Sin(angle);

                for (int i = 0; i < n; i += len)
                {
                    double curRe = 1.0, curIm = 0.0;
                    for (int k = 0; k < len / 2; k++)
                    {
                        int a = i + k;
                        int b = i + k + len / 2;

                        double tRe = re[b] * curRe - im[b] * curIm;
                        double tIm = re[b] * curIm + im[b] * curRe;

                        re[b] = re[a] - tRe;
                        im[b] = im[a] - tIm;
                        re[a] += tRe;
                        im[a] += tIm;

                        double nextRe = curRe * wRe - curIm * wIm;
                        curIm = curRe * wIm + curIm * wRe;
                        curRe = nextRe;
                    }
                }
            }

            if (invert)
            {
                for (int i = 0; i < n; i++)
                {
                    re[i] /= n;
                    im[i] /= n;
                }
            }
        }

        #endregion

        /// <summary>
        /// Read the next byte of the key stream.
        /// Reset the stream if it is too short.
        /// </summary>
        /// <param name="keyStream">The key stream</param>
        /// <returns>The next key byte</returns>
        private static byte GetKeyValue(Stream keyStream)
        {
            int keyValue;
            if ((keyValue = keyStream.ReadByte()) < 0)
            {
                keyStream.Seek(0, SeekOrigin.Begin);
                keyValue = keyStream.ReadByte();
                if (keyValue == 0) { keyValue = 1; }
            }
            return (byte)keyValue;
        }
    }
}
