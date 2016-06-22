﻿using Concentus.Common;
using Concentus.Common.CPlusPlus;
using Concentus.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Concentus.Structs
{
    public class OpusPacketInfo
    {
        /// <summary>
        /// The Table of Contents byte for this packet. Contains info about modes, frame length, etc.
        /// </summary>
        public readonly byte TOCByte;

        /// <summary>
        /// The list of subframes in this packet
        /// </summary>
        public readonly IList<byte[]> Frames;

        /// <summary>
        /// The index of the start of the payload within the packet
        /// </summary>
        public readonly int PayloadOffset;

        private OpusPacketInfo(byte toc, IList<byte[]> frames, int payloadOffset)
        {
            TOCByte = toc;
            Frames = frames;
            PayloadOffset = payloadOffset;
        }

        /// <summary>
        /// Parse an opus packet into a packetinfo object containing one or more frames.
        /// Opus_decode will perform this operation internally so most applications do
        /// not need to use this function.
        /// </summary>
        /// <param name="data">The packet data to be parsed</param>
        /// <param name="data_offset">The index of the beginning of the packet in the data array (usually 0)</param>
        /// <param name="len">The packet's length</param>
        /// <returns>A parsed packet info struct</returns>
        public static OpusPacketInfo ParseOpusPacket(byte[] data, int data_offset, int len)
        {
            // Find the number of frames first
            int numFrames = GetNumFrames(data.GetPointer(data_offset), len);

            BoxedValue<int> payload_offset = new BoxedValue<int>();
            BoxedValue<byte> out_toc = new BoxedValue<byte>();
            Pointer<Pointer<byte>> frames = Pointer.Malloc<Pointer<byte>>(numFrames);
            Pointer<short> size = Pointer.Malloc<short>(numFrames);
            BoxedValue<int> packetOffset = new BoxedValue<int>();
            int error = opus_packet_parse_impl(data.GetPointer(data_offset), len, 0, out_toc, frames, size, payload_offset, packetOffset);
            if (error < 0)
            {
                throw new OpusException("An error occurred while parsing the packet: " + CodecHelpers.opus_strerror(error));
            }

            IList<byte[]> copiedFrames = new List<byte[]>();

            for (int c = 0; c < numFrames; c++)
            {
                byte[] nextFrame = new byte[size[c]];
                frames[c].MemCopyTo(nextFrame.GetPointer(), nextFrame.Length);
                copiedFrames.Add(nextFrame);
            }

            return new OpusPacketInfo(out_toc.Val, copiedFrames, payload_offset.Val);
        }
        
        /// <summary>
        /// Gets the number of samples per frame from an Opus packet.
        /// </summary>
        /// <param name="data">Opus packet. This must contain at least one byte of data</param>
        /// <param name="Fs">Sampling rate in Hz. This must be a multiple of 400, or inaccurate results will be returned.</param>
        /// <returns>Number of samples per frame</returns>
        public static int GetNumSamplesPerFrame(Pointer<byte> data, int Fs)
        {
            int audiosize;
            if ((data[0] & 0x80) != 0)
            {
                audiosize = ((data[0] >> 3) & 0x3);
                audiosize = (Fs << audiosize) / 400;
            }
            else if ((data[0] & 0x60) == 0x60)
            {
                audiosize = ((data[0] & 0x08) != 0) ? Fs / 50 : Fs / 100;
            }
            else
            {
                audiosize = ((data[0] >> 3) & 0x3);
                if (audiosize == 3)
                    audiosize = Fs * 60 / 1000;
                else
                    audiosize = (Fs << audiosize) / 100;
            }
            return audiosize;
        }

        /// <summary>
        /// Gets the encoded bandwidth of an Opus packet. Note that you are not forced to decode at this bandwidth
        /// </summary>
        /// <param name="data">An Opus packet (must be at least 1 byte)</param>
        /// <returns>An OpusBandwidth value</returns>
        public static int GetBandwidth(Pointer<byte> data)
        {
            int bandwidth;
            if ((data[0] & 0x80) != 0)
            {
                bandwidth = OpusBandwidth.OPUS_BANDWIDTH_MEDIUMBAND + ((data[0] >> 5) & 0x3);
                if (bandwidth == OpusBandwidth.OPUS_BANDWIDTH_MEDIUMBAND)
                    bandwidth = OpusBandwidth.OPUS_BANDWIDTH_NARROWBAND;
            }
            else if ((data[0] & 0x60) == 0x60)
            {
                bandwidth = ((data[0] & 0x10) != 0) ? OpusBandwidth.OPUS_BANDWIDTH_FULLBAND :
                                             OpusBandwidth.OPUS_BANDWIDTH_SUPERWIDEBAND;
            }
            else {
                bandwidth = OpusBandwidth.OPUS_BANDWIDTH_NARROWBAND + ((data[0] >> 5) & 0x3);
            }
            return bandwidth;
        }

        /// <summary>
        /// Gets the number of encoded channels of an Opus packet. Note that you are not forced to decode with this channel count.
        /// </summary>
        /// <param name="data">An opus packet (must be at least 1 byte)</param>
        /// <returns>The number of channels</returns>
        public static int GetNumEncodedChannels(Pointer<byte> data)
        {
            return ((data[0] & 0x4) != 0) ? 2 : 1;
        }

        /// <summary>
        /// Gets the number of frames in an Opus packet.
        /// </summary>
        /// <param name="packet">An Opus packet</param>
        /// <param name="len">The packet's length (must be at least 1)</param>
        /// <returns>The number of frames in the packet</returns>
        public static int GetNumFrames(Pointer<byte> packet, int len)
        {
            int count;
            if (len < 1)
                return OpusError.OPUS_BAD_ARG;
            count = packet[0] & 0x3;
            if (count == 0)
                return 1;
            else if (count != 3)
                return 2;
            else if (len < 2)
                return OpusError.OPUS_INVALID_PACKET;
            else
                return packet[1] & 0x3F;
        }

        /// <summary>
        /// Gets the number of samples of an Opus packet.
        /// </summary>
        /// <param name="packet">An Opus packet</param>
        /// <param name="len">The packet's length</param>
        /// <param name="Fs">The decoder's sampling rate in Hz. This must be a multiple of 400</param>
        /// <returns>The size of the PCM samples that this packet will be decoded to at the specified sample rate</returns>
        public static int GetNumSamples(Pointer<byte> packet, int len,
              int Fs)
        {
            int samples;
            int count = GetNumFrames(packet, len);

            if (count < 0)
                return count;

            samples = count * GetNumSamplesPerFrame(packet, Fs);
            /* Can't have more than 120 ms */
            if (samples * 25 > Fs * 3)
                return OpusError.OPUS_INVALID_PACKET;
            else
                return samples;
        }

        /// <summary>
        /// Gets the number of samples of an Opus packet.
        /// </summary>
        /// <param name="dec">Your current decoder state</param>
        /// <param name="packet">An Opus packet</param>
        /// <param name="len">The packet's length</param>
        /// <returns>The size of the PCM samples that this packet will be decoded to by the specified decoder</returns>
        public static int GetNumSamples(OpusDecoder dec,
              Pointer<byte> packet, int len)
        {
            return GetNumSamples(packet, len, dec.Fs);
        }

        /// <summary>
        /// Gets the mode that was used to encode this packet.
        /// Normally there is nothing you can really do with this, other than debugging.
        /// </summary>
        /// <param name="data">An Opus packet</param>
        /// <returns>The OpusMode used by the encoder</returns>
        public static OpusMode GetEncoderMode(Pointer<byte> data)
        {
            OpusMode mode;
            if ((data[0] & 0x80) != 0)
            {
                mode = OpusMode.MODE_CELT_ONLY;
            }
            else if ((data[0] & 0x60) == 0x60)
            {
                mode = OpusMode.MODE_HYBRID;
            }
            else {
                mode = OpusMode.MODE_SILK_ONLY;
            }
            return mode;
        }

        internal static int encode_size(int size, Pointer<byte> data)
        {
            if (size < 252)
            {
                data[0] = (byte)size;
                return 1;
            }
            else {
                data[0] = (byte)(252 + (size & 0x3));
                data[1] = (byte)((size - (int)data[0]) >> 2);
                return 2;
            }
        }

        internal static int parse_size(Pointer<byte> data, int len, BoxedValue<short> size)
        {
            if (len < 1)
            {
                size.Val = -1;
                return -1;
            }
            else if (data[0] < 252)
            {
                size.Val = data[0];
                return 1;
            }
            else if (len < 2)
            {
                size.Val = -1;
                return -1;
            }
            else {
                size.Val = Inlines.CHOP16(4 * data[1] + data[0]);
                return 2;
            }
        }

        internal static int opus_packet_parse_impl(Pointer<byte> data, int len,
              int self_delimited, BoxedValue<byte> out_toc,
              Pointer<Pointer<byte>> frames, Pointer<short> size,
              BoxedValue<int> payload_offset, BoxedValue<int> packet_offset)
        {
            int i, bytes;
            int count;
            int cbr;
            byte ch, toc;
            int framesize;
            int last_size;
            int pad = 0;
            Pointer<byte> data0 = data;

            if (size == null)
                return OpusError.OPUS_BAD_ARG;

            framesize = GetNumSamplesPerFrame(data, 48000);

            cbr = 0;
            toc = data[0];
            data = data.Point(1);
            len--;
            last_size = len;
            switch (toc & 0x3)
            {
                /* One frame */
                case 0:
                    count = 1;
                    break;
                /* Two CBR frames */
                case 1:
                    count = 2;
                    cbr = 1;
                    if (self_delimited == 0)
                    {
                        if ((len & 0x1) != 0)
                            return OpusError.OPUS_INVALID_PACKET;
                        last_size = len / 2;
                        /* If last_size doesn't fit in size[0], we'll catch it later */
                        size[0] = (short)last_size;
                    }
                    break;
                /* Two VBR frames */
                case 2:
                    count = 2;
                    BoxedValue<short> boxed_size = new BoxedValue<short>(size[0]);
                    bytes = parse_size(data, len, boxed_size);
                    size[0] = boxed_size.Val;
                    len -= bytes;
                    if (size[0] < 0 || size[0] > len)
                        return OpusError.OPUS_INVALID_PACKET;
                    data = data.Point(bytes);
                    last_size = len - size[0];
                    break;
                /* Multiple CBR/VBR frames (from 0 to 120 ms) */
                default: /*case 3:*/
                    if (len < 1)
                        return OpusError.OPUS_INVALID_PACKET;
                    /* Number of frames encoded in bits 0 to 5 */
                    ch = data[0];
                    data = data.Point(1);
                    count = ch & 0x3F;
                    if (count <= 0 || framesize * count > 5760)
                        return OpusError.OPUS_INVALID_PACKET;
                    len--;
                    /* Padding flag is bit 6 */
                    if ((ch & 0x40) != 0)
                    {
                        int p;
                        do
                        {
                            int tmp;
                            if (len <= 0)
                                return OpusError.OPUS_INVALID_PACKET;
                            p = data[0];
                            data = data.Point(1);
                            len--;
                            tmp = p == 255 ? 254 : p;
                            len -= tmp;
                            pad += tmp;
                        } while (p == 255);
                    }
                    if (len < 0)
                        return OpusError.OPUS_INVALID_PACKET;
                    /* VBR flag is bit 7 */
                    cbr = (ch & 0x80) != 0 ? 0 : 1;
                    if (cbr == 0)
                    {
                        /* VBR case */
                        last_size = len;
                        for (i = 0; i < count - 1; i++)
                        {
                            boxed_size = new BoxedValue<short>(size[i]);
                            bytes = parse_size(data, len, boxed_size);
                            size[i] = boxed_size.Val;
                            len -= bytes;
                            if (size[i] < 0 || size[i] > len)
                                return OpusError.OPUS_INVALID_PACKET;
                            data = data.Point(bytes);
                            last_size -= bytes + size[i];
                        }
                        if (last_size < 0)
                            return OpusError.OPUS_INVALID_PACKET;
                    }
                    else if (self_delimited == 0)
                    {
                        /* CBR case */
                        last_size = len / count;
                        if (last_size * count != len)
                            return OpusError.OPUS_INVALID_PACKET;
                        for (i = 0; i < count - 1; i++)
                            size[i] = (short)last_size;
                    }
                    break;
            }

            /* Self-delimited framing has an extra size for the last frame. */
            if (self_delimited != 0)
            {
                BoxedValue<short> boxed_size = new BoxedValue<short>(size[count - 1]);
                bytes = parse_size(data, len, boxed_size);
                size[count - 1] = boxed_size.Val;
                len -= bytes;
                if (size[count - 1] < 0 || size[count - 1] > len)
                    return OpusError.OPUS_INVALID_PACKET;
                data = data.Point(bytes);
                /* For CBR packets, apply the size to all the frames. */
                if (cbr != 0)
                {
                    if (size[count - 1] * count > len)
                        return OpusError.OPUS_INVALID_PACKET;
                    for (i = 0; i < count - 1; i++)
                        size[i] = size[count - 1];
                }
                else if (bytes + size[count - 1] > last_size)
                    return OpusError.OPUS_INVALID_PACKET;
            }
            else
            {
                /* Because it's not encoded explicitly, it's possible the size of the
                   last packet (or all the packets, for the CBR case) is larger than
                   1275. Reject them here.*/
                if (last_size > 1275)
                    return OpusError.OPUS_INVALID_PACKET;
                size[count - 1] = (short)last_size;
            }

            if (payload_offset != null)
                payload_offset.Val = (int)(data.Offset - data0.Offset);

            for (i = 0; i < count; i++)
            {
                if (frames != null)
                    frames[i] = data;
                data = data.Point(size[i]);
            }

            if (packet_offset != null)
                packet_offset.Val = pad + (int)(data.Offset - data0.Offset);

            if (out_toc != null)
                out_toc.Val = toc;

            return count;
        }

        // used internally
        internal static int opus_packet_parse(Pointer<byte> data, int len,
              BoxedValue<byte> out_toc, Pointer<Pointer<byte>> frames,
              Pointer<short> size, BoxedValue<int> payload_offset)
        {
            return OpusPacketInfo.opus_packet_parse_impl(data, len, 0, out_toc,
                                          frames, size, payload_offset, null);
        }
    }
}