using System;
using System.IO;

namespace SteganoWave
{
    /// <summary>Hides/extracts data in/from a wave stream</summary>
    public class WaveUtility
    {
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
        }

        /// <summary>
        /// Hide [messageStream] in [sourceStream],
        /// write the result to [destinationStream]
        /// </summary>
        /// <param name="messageStream">The message to hide</param>
        /// <param name="keyStream">
        /// A key stream that specifies how many samples shall be
        /// left clean between two changed samples
        /// </param>
        public void Hide(Stream messageStream, Stream keyStream)
        {
            byte[] sample = new byte[bytesPerSample];

            // make sure we read the message from the start
            messageStream.Seek(0, SeekOrigin.Begin);

            int currentByte;
            // walk through every byte of the message (length prefix included)
            while ((currentByte = messageStream.ReadByte()) >= 0)
            {
                // hide the 8 bits of the byte, least significant bit first
                for (int bitIndex = 0; bitIndex < 8; bitIndex++)
                {
                    int messageBit = (currentByte >> bitIndex) & 1;

                    // the key tells us how many samples this bit "costs":
                    // (distance - 1) samples stay clean, the last one carries the bit
                    int distance = GetKeyValue(keyStream);

                    // copy the clean samples unchanged into the destination
                    for (int i = 0; i < distance - 1; i++)
                    {
                        if (sourceStream.Copy(sample, 0, bytesPerSample, destinationStream) <= 0)
                            throw new Exception("Unexpected end of carrier stream while hiding.");
                    }

                    // read the sample that will carry the bit
                    if (sourceStream.Read(sample, 0, bytesPerSample) <= 0)
                        throw new Exception("Unexpected end of carrier stream while hiding.");

                    // store the message bit in the least significant bit of the sample
                    sample[0] = (byte)((sample[0] & 0xFE) | messageBit);

                    // write the modified sample to the destination
                    destinationStream.Write(sample, 0, bytesPerSample);
                }
            }

            // copy whatever is left of the carrier wave unchanged,
            // so the output keeps the full length declared in the header
            while (sourceStream.Copy(sample, 0, bytesPerSample, destinationStream) > 0)
            {
                // keep copying until the data chunk is exhausted
            }
        }

        /// <summary>Extract a message from [sourceStream] into [messageStream]</summary>
        /// <param name="messageStream">Empty stream to receive the extracted message</param>
        /// <param name="keyStream">
        /// A key stream that specifies how many samples shall be
        /// skipped between two carrier samples
        /// </param>
        public void Extract(Stream messageStream, Stream keyStream)
        {
            byte[] sample = new byte[bytesPerSample];

            // The message was stored as [4-byte length][message bytes].
            // We don't know how long it is until we have decoded the first 4 bytes.
            byte[] header = new byte[4];
            long totalBytes = header.Length; // at least the length prefix

            for (long byteIndex = 0; byteIndex < totalBytes; byteIndex++)
            {
                // rebuild one byte out of 8 sample LSBs (least significant bit first)
                int currentByte = 0;
                for (int bitIndex = 0; bitIndex < 8; bitIndex++)
                {
                    // the key tells us how many samples this bit "costs":
                    // (distance - 1) clean samples are skipped, the last one carries the bit
                    int distance = GetKeyValue(keyStream);

                    for (int i = 0; i < distance - 1; i++)
                    {
                        if (sourceStream.Read(sample, 0, bytesPerSample) <= 0)
                            throw new Exception("Unexpected end of carrier stream while extracting.");
                    }

                    if (sourceStream.Read(sample, 0, bytesPerSample) <= 0)
                        throw new Exception("Unexpected end of carrier stream while extracting.");

                    currentByte |= ((sample[0] & 1) << bitIndex);
                }

                if (byteIndex < header.Length)
                {
                    // still reading the length prefix
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
                    // these are the actual message bytes
                    messageStream.WriteByte((byte)currentByte);
                }
            }
        }

        /// <summary>Counts the samples that will be skipped using the specified key stream</summary>
        /// <param name="keyStream">Key stream</param>
        /// <param name="messageLength">Length of the message</param>
        /// <returns>Minimum length (in samples) of an audio file</returns>
        public static long CheckKeyForMessage(Stream keyStream, long messageLength)
        {
            long messageLengthBits = messageLength * 8;
            long countRequiredSamples = 0;

            if (messageLengthBits > keyStream.Length)
            {
                long keyLength = keyStream.Length;

                // read existing key
                byte[] keyBytes = new byte[keyLength];
                keyStream.Read(keyBytes, 0, keyBytes.Length);

                // Every byte stands for the distance between two useable samples.
                // The sum of those distances is the required count of samples.
                countRequiredSamples = SumKeyArray(keyBytes);

                // The key must be repeated, until every bit of the message has a key byte.
                double countKeyCopies = messageLengthBits / keyLength;
                countRequiredSamples = (long)(countRequiredSamples * countKeyCopies);
            }
            else
            {
                byte[] keyBytes = new byte[messageLengthBits];
                keyStream.Read(keyBytes, 0, keyBytes.Length);
                countRequiredSamples = SumKeyArray(keyBytes);
            }

            keyStream.Seek(0, SeekOrigin.Begin);
            return countRequiredSamples;
        }

        private static long SumKeyArray(byte[] values)
        {
            long sum = 0;
            foreach (int value in values)
            {	// '0' causes a distance of one sample,
                // every other key causes a distance of its exact value.
                sum += (value == 0) ? 1 : value;
            }
            return sum;
        }

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