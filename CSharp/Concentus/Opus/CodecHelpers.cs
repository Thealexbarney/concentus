﻿using Concentus.Celt;
using Concentus.Celt.Structs;
using Concentus.Common;
using Concentus.Common.CPlusPlus;
using Concentus;
using Concentus.Enums;
using Concentus.Silk;
using Concentus.Silk.Structs;
using Concentus.Structs;
using System;

namespace Concentus
{
    public static class CodecHelpers
    {
        internal static byte gen_toc(OpusMode mode, int framerate, int bandwidth, int channels)
        {
            int period;
            byte toc;
            period = 0;
            while (framerate < 400)
            {
                framerate <<= 1;
                period++;
            }
            if (mode == OpusMode.MODE_SILK_ONLY)
            {
                toc = Inlines.CHOP8U((bandwidth - OpusBandwidth.OPUS_BANDWIDTH_NARROWBAND) << 5);
                toc |= Inlines.CHOP8U((period - 2) << 3);
            }
            else if (mode == OpusMode.MODE_CELT_ONLY)
            {
                int tmp = bandwidth - OpusBandwidth.OPUS_BANDWIDTH_MEDIUMBAND;
                if (tmp < 0)
                    tmp = 0;
                toc = 0x80;
                toc |= Inlines.CHOP8U(tmp << 5);
                toc |= Inlines.CHOP8U(period << 3);
            }
            else /* Hybrid */
            {
                toc = 0x60;
                toc |= Inlines.CHOP8U((bandwidth - OpusBandwidth.OPUS_BANDWIDTH_SUPERWIDEBAND) << 4);
                toc |= Inlines.CHOP8U((period - 2) << 3);
            }
            toc |= Inlines.CHOP8U((channels == 2 ? 1 : 0) << 2);
            return toc;
        }

        internal static void hp_cutoff(Pointer<short> input, int cutoff_Hz, Pointer<int> output, Pointer<int> hp_mem, int len, int channels, int Fs)
        {
            Pointer<int> B_Q28 = Pointer.Malloc<int>(3);
            Pointer<int> A_Q28 = Pointer.Malloc<int>(2);
            int Fc_Q19, r_Q28, r_Q22;

            Inlines.OpusAssert(cutoff_Hz <= int.MaxValue / Inlines.SILK_CONST(1.5f * 3.14159f / 1000, 19));
            Fc_Q19 = Inlines.silk_DIV32_16(Inlines.silk_SMULBB(Inlines.SILK_CONST(1.5f * 3.14159f / 1000, 19), cutoff_Hz), Fs / 1000);
            Inlines.OpusAssert(Fc_Q19 > 0 && Fc_Q19 < 32768);

            r_Q28 = Inlines.SILK_CONST(1.0f, 28) - Inlines.silk_MUL(Inlines.SILK_CONST(0.92f, 9), Fc_Q19);

            /* b = r * [ 1; -2; 1 ]; */
            /* a = [ 1; -2 * r * ( 1 - 0.5 * Fc^2 ); r^2 ]; */
            B_Q28[0] = r_Q28;
            B_Q28[1] = Inlines.silk_LSHIFT(-r_Q28, 1);
            B_Q28[2] = r_Q28;

            /* -r * ( 2 - Fc * Fc ); */
            r_Q22 = Inlines.silk_RSHIFT(r_Q28, 6);
            A_Q28[0] = Inlines.silk_SMULWW(r_Q22, Inlines.silk_SMULWW(Fc_Q19, Fc_Q19) - Inlines.SILK_CONST(2.0f, 22));
            A_Q28[1] = Inlines.silk_SMULWW(r_Q22, r_Q22);

            Filters.silk_biquad_alt(input, B_Q28, A_Q28, hp_mem, output, len, channels);
            if (channels == 2)
            {
                Filters.silk_biquad_alt(input.Point(1), B_Q28, A_Q28, hp_mem.Point(2), output.Point(1), len, channels);
            }
        }

        internal static void dc_reject(Pointer<short> input, int cutoff_Hz, Pointer<int> output, Pointer<int> hp_mem, int len, int channels, int Fs)
        {
            int c, i;
            int shift;

            /* Approximates -round(log2(4.*cutoff_Hz/Fs)) */
            shift = Inlines.celt_ilog2(Fs / (cutoff_Hz * 3));
            for (c = 0; c < channels; c++)
            {
                for (i = 0; i < len; i++)
                {
                    int x, tmp, y;
                    x = Inlines.SHL32(Inlines.EXTEND32(input[channels * i + c]), 15);
                    /* First stage */
                    tmp = x - hp_mem[2 * c];
                    hp_mem[2 * c] = hp_mem[2 * c] + Inlines.PSHR32(x - hp_mem[2 * c], shift);
                    /* Second stage */
                    y = tmp - hp_mem[2 * c + 1];
                    hp_mem[2 * c + 1] = hp_mem[2 * c + 1] + Inlines.PSHR32(tmp - hp_mem[2 * c + 1], shift);
                    output[channels * i + c] = Inlines.EXTRACT16(Inlines.SATURATE(Inlines.PSHR32(y, 15), 32767));
                }
            }
        }

        internal static void stereo_fade(Pointer<int> input, Pointer<int> output, int g1, int g2,
                int overlap48, int frame_size, int channels, Pointer<int> window, int Fs)
        {
            int i;
            int overlap;
            int inc;
            inc = 48000 / Fs;
            overlap = overlap48 / inc;
            g1 = CeltConstants.Q15ONE - g1;
            g2 = CeltConstants.Q15ONE - g2;
            for (i = 0; i < overlap; i++)
            {
                int diff;
                int g, w;
                w = Inlines.MULT16_16_Q15(window[i * inc], window[i * inc]);
                g = Inlines.SHR32(Inlines.MAC16_16(Inlines.MULT16_16(w, g2),
                      CeltConstants.Q15ONE - w, g1), 15);
                diff = Inlines.EXTRACT16(Inlines.HALF32((int)input[i * channels] - (int)input[i * channels + 1]));
                diff = Inlines.MULT16_16_Q15(g, diff);
                output[i * channels] = output[i * channels] - diff;
                output[i * channels + 1] = output[i * channels + 1] + diff;
            }
            for (; i < frame_size; i++)
            {
                int diff;
                diff = Inlines.EXTRACT16(Inlines.HALF32((int)input[i * channels] - (int)input[i * channels + 1]));
                diff = Inlines.MULT16_16_Q15(g2, diff);
                output[i * channels] = output[i * channels] - diff;
                output[i * channels + 1] = output[i * channels + 1] + diff;
            }
        }

        internal static void gain_fade(Pointer<int> input, Pointer<int> output, int g1, int g2,
                int overlap48, int frame_size, int channels, Pointer<int> window, int Fs)
        {
            int i;
            int inc;
            int overlap;
            int c;
            inc = 48000 / Fs;
            overlap = overlap48 / inc;
            if (channels == 1)
            {
                for (i = 0; i < overlap; i++)
                {
                    int g, w;
                    w = Inlines.MULT16_16_Q15(window[i * inc], window[i * inc]);
                    g = Inlines.SHR32(Inlines.MAC16_16(Inlines.MULT16_16(w, g2),
                          CeltConstants.Q15ONE - w, g1), 15);
                    output[i] = Inlines.MULT16_16_Q15(g, input[i]);
                }
            }
            else {
                for (i = 0; i < overlap; i++)
                {
                    int g, w;
                    w = Inlines.MULT16_16_Q15(window[i * inc], window[i * inc]);
                    g = Inlines.SHR32(Inlines.MAC16_16(Inlines.MULT16_16(w, g2),
                                    CeltConstants.Q15ONE - w, g1), 15);
                    output[i * 2] = Inlines.MULT16_16_Q15(g, input[i * 2]);
                    output[i * 2 + 1] = Inlines.MULT16_16_Q15(g, input[i * 2 + 1]);
                }
            }
            c = 0; do
            {
                for (i = overlap; i < frame_size; i++)
                {
                    output[i * channels + c] = Inlines.MULT16_16_Q15(g2, input[i * channels + c]);
                }
            }
            while (++c < channels);
        }

        /* Don't use more than 60 ms for the frame size analysis */
        private const int MAX_DYNAMIC_FRAMESIZE = 24;

        /* Estimates how much the bitrate will be boosted based on the sub-frame energy */
        internal static float transient_boost(Pointer<float> E, Pointer<float> E_1, int LM, int maxM)
        {
            int i;
            int M;
            float sumE = 0, sumE_1 = 0;
            float metric;

            M = Inlines.IMIN(maxM, (1 << LM) + 1);
            for (i = 0; i < M; i++)
            {
                sumE += E[i];
                sumE_1 += E_1[i];
            }
            metric = sumE * sumE_1 / (M * M);
            /*if (LM==3)
               printf("%f\n", metric);*/
            /*return metric>10 ? 1 : 0;*/
            /*return Inlines.MAX16(0,1-exp(-.25*(metric-2.)));*/
            return Inlines.MIN16(1, (float)Math.Sqrt(Inlines.MAX16(0, .05f * (metric - 2))));
        }

        /* Viterbi decoding trying to find the best frame size combination using look-ahead

           State numbering:
            0: unused
            1:  2.5 ms
            2:  5 ms (#1)
            3:  5 ms (#2)
            4: 10 ms (#1)
            5: 10 ms (#2)
            6: 10 ms (#3)
            7: 10 ms (#4)
            8: 20 ms (#1)
            9: 20 ms (#2)
           10: 20 ms (#3)
           11: 20 ms (#4)
           12: 20 ms (#5)
           13: 20 ms (#6)
           14: 20 ms (#7)
           15: 20 ms (#8)
        */
        internal static int transient_viterbi(Pointer<float> E, Pointer<float> E_1, int N, int frame_cost, int rate)
        {
            int i;
            Pointer<Pointer<float>> cost = Arrays.InitTwoDimensionalArrayPointer<float>(MAX_DYNAMIC_FRAMESIZE, 16);
            Pointer<Pointer<int>> states = Arrays.InitTwoDimensionalArrayPointer<int>(MAX_DYNAMIC_FRAMESIZE, 16);
            float best_cost;
            int best_state;
            float factor;
            /* Take into account that we damp VBR in the 32 kb/s to 64 kb/s range. */
            if (rate < 80)
                factor = 0;
            else if (rate > 160)
                factor = 1;
            else
                factor = (rate - 80.0f) / 80.0f;
            /* Makes variable framesize less aggressive at lower bitrates, but I can't
               find any valid theoretical justification for this (other than it seems
               to help) */
            for (i = 0; i < 16; i++)
            {
                /* Impossible state */
                states[0][i] = -1;
                cost[0][i] = 1e10f;
            }
            for (i = 0; i < 4; i++)
            {
                cost[0][1 << i] = (frame_cost + rate * (1 << i)) * (1 + factor * transient_boost(E, E_1, i, N + 1));
                states[0][1 << i] = i;
            }
            for (i = 1; i < N; i++)
            {
                int j;

                /* Follow continuations */
                for (j = 2; j < 16; j++)
                {
                    cost[i][j] = cost[i - 1][j - 1];
                    states[i][j] = j - 1;
                }

                /* New frames */
                for (j = 0; j < 4; j++)
                {
                    int k;
                    float min_cost;
                    float curr_cost;
                    states[i][1 << j] = 1;
                    min_cost = cost[i - 1][1];
                    for (k = 1; k < 4; k++)
                    {
                        float tmp = cost[i - 1][(1 << (k + 1)) - 1];
                        if (tmp < min_cost)
                        {
                            states[i][1 << j] = (1 << (k + 1)) - 1;
                            min_cost = tmp;
                        }
                    }
                    curr_cost = (frame_cost + rate * (1 << j)) * (1 + factor * transient_boost(E.Point(i), E_1.Point(i), j, N - i + 1));
                    cost[i][1 << j] = min_cost;
                    /* If part of the frame is outside the analysis window, only count part of the cost */
                    if (N - i < (1 << j))
                        cost[i][1 << j] += curr_cost * (float)(N - i) / (1 << j);
                    else
                        cost[i][1 << j] += curr_cost;
                }
            }

            best_state = 1;
            best_cost = cost[N - 1][1];
            /* Find best end state (doesn't force a frame to end at N-1) */
            for (i = 2; i < 16; i++)
            {
                if (cost[N - 1][i] < best_cost)
                {
                    best_cost = cost[N - 1][i];
                    best_state = i;
                }
            }

            /* Follow transitions back */
            for (i = N - 1; i >= 0; i--)
            {
                /*printf("%d ", best_state);*/
                best_state = states[i][best_state];
            }
            /*printf("%d\n", best_state);*/
            return best_state;
        }

        internal static int optimize_framesize<T>(Pointer<T> x, int len, int C, int Fs,
                        int bitrate, int tonality, Pointer<float> mem, int buffering,
                        Downmix.downmix_func<T> downmix)
        {
            int N;
            int i;
            float[] e = new float[MAX_DYNAMIC_FRAMESIZE + 4];
            float[] e_1 = new float[MAX_DYNAMIC_FRAMESIZE + 3];
            int memx;
            int bestLM = 0;
            int subframe;
            int pos;
            int offset;
            Pointer<int> sub;

            subframe = Fs / 400;
            sub = Pointer.Malloc<int>(subframe);
            e[0] = mem[0];
            e_1[0] = 1.0f / (CeltConstants.EPSILON + mem[0]);
            if (buffering != 0)
            {
                /* Consider the CELT delay when not in restricted-lowdelay */
                /* We assume the buffering is between 2.5 and 5 ms */
                offset = 2 * subframe - buffering;
                Inlines.OpusAssert(offset >= 0 && offset <= subframe);
                len -= offset;
                e[1] = mem[1];
                e_1[1] = 1.0f / (CeltConstants.EPSILON + mem[1]);
                e[2] = mem[2];
                e_1[2] = 1.0f / (CeltConstants.EPSILON + mem[2]);
                pos = 3;
            }
            else {
                pos = 1;
                offset = 0;
            }
            N = Inlines.IMIN(len / subframe, MAX_DYNAMIC_FRAMESIZE);
            /* Just silencing a warning, it's really initialized later */
            memx = 0;
            for (i = 0; i < N; i++)
            {
                float tmp;
                int tmpx;
                int j;
                tmp = CeltConstants.EPSILON;

                downmix(x, sub, subframe, i * subframe + offset, 0, -2, C);
                if (i == 0)
                    memx = sub[0];
                for (j = 0; j < subframe; j++)
                {
                    tmpx = sub[j];
                    tmp += (tmpx - memx) * (float)(tmpx - memx);
                    memx = tmpx;
                }
                e[i + pos] = tmp;
                e_1[i + pos] = 1.0f / tmp;
            }
            /* Hack to get 20 ms working with APPLICATION_AUDIO
               The real problem is that the corresponding memory needs to use 1.5 ms
               from this frame and 1 ms from the next frame */
            e[i + pos] = e[i + pos - 1];
            if (buffering != 0)
                N = Inlines.IMIN(MAX_DYNAMIC_FRAMESIZE, N + 2);
            bestLM = transient_viterbi(e.GetPointer(), e_1.GetPointer(), N, (int)((1.0f + .5f * tonality) * (60 * C + 40)), bitrate / 400);
            mem[0] = e[1 << bestLM];
            if (buffering != 0)
            {
                mem[1] = e[(1 << bestLM) + 1];
                mem[2] = e[(1 << bestLM) + 2];
            }
            return bestLM;
        }

        internal static int frame_size_select(int frame_size, OpusFramesize variable_duration, int Fs)
        {
            int new_size;
            if (frame_size < Fs / 400)
                return -1;
            if (variable_duration == OpusFramesize.OPUS_FRAMESIZE_ARG)
                new_size = frame_size;
            else if (variable_duration == OpusFramesize.OPUS_FRAMESIZE_VARIABLE)
                new_size = Fs / 50;
            else if (variable_duration >= OpusFramesize.OPUS_FRAMESIZE_2_5_MS && variable_duration <= OpusFramesize.OPUS_FRAMESIZE_60_MS)
                new_size = Inlines.IMIN(3 * Fs / 50, (Fs / 400) << (variable_duration - OpusFramesize.OPUS_FRAMESIZE_2_5_MS));
            else
                return -1;
            if (new_size > frame_size)
                return -1;
            if (400 * new_size != Fs && 200 * new_size != Fs && 100 * new_size != Fs &&
                     50 * new_size != Fs && 25 * new_size != Fs && 50 * new_size != 3 * Fs)
                return -1;
            return new_size;
        }

        internal static int compute_frame_size<T>(Pointer<T> analysis_pcm, int frame_size,
              OpusFramesize variable_duration, int C, int Fs, int bitrate_bps,
              int delay_compensation, Downmix.downmix_func<T> downmix
#if ENABLE_ANALYSIS
              , Pointer<float> subframe_mem
#endif
              )
        {
#if ENABLE_ANALYSIS
            if (variable_duration == OpusFramesize.OPUS_FRAMESIZE_VARIABLE && frame_size >= Fs / 200)
            {
                int LM = 3;
                LM = optimize_framesize(analysis_pcm, frame_size, C, Fs, bitrate_bps,
                      0, subframe_mem, delay_compensation, downmix);
                while ((Fs / 400 << LM) > frame_size)
                    LM--;
                frame_size = (Fs / 400 << LM);
            }
            else
#endif
            {
                frame_size = frame_size_select(frame_size, variable_duration, Fs);
            }

            if (frame_size < 0)
                return -1;
            return frame_size;
        }

        internal static int compute_stereo_width(Pointer<short> pcm, int frame_size, int Fs, StereoWidthState mem)
        {
            int corr;
            int ldiff;
            int width;
            int xx, xy, yy;
            int sqrt_xx, sqrt_yy;
            int qrrt_xx, qrrt_yy;
            int frame_rate;
            int i;
            int short_alpha;

            frame_rate = Fs / frame_size;
            // fixme ghetto order of ops
            short_alpha = CeltConstants.Q15ONE - 25 * CeltConstants.Q15ONE / Inlines.IMAX(50, frame_rate);
            xx = xy = yy = 0;
            for (i = 0; i < frame_size; i += 4)
            {
                int pxx = 0;
                int pxy = 0;
                int pyy = 0;
                int x, y;
                x = pcm[2 * i];
                y = pcm[2 * i + 1];
                pxx = Inlines.SHR32(Inlines.MULT16_16(x, x), 2);
                pxy = Inlines.SHR32(Inlines.MULT16_16(x, y), 2);
                pyy = Inlines.SHR32(Inlines.MULT16_16(y, y), 2);
                x = pcm[2 * i + 2];
                y = pcm[2 * i + 3];
                pxx += Inlines.SHR32(Inlines.MULT16_16(x, x), 2);
                pxy += Inlines.SHR32(Inlines.MULT16_16(x, y), 2);
                pyy += Inlines.SHR32(Inlines.MULT16_16(y, y), 2);
                x = pcm[2 * i + 4];
                y = pcm[2 * i + 5];
                pxx += Inlines.SHR32(Inlines.MULT16_16(x, x), 2);
                pxy += Inlines.SHR32(Inlines.MULT16_16(x, y), 2);
                pyy += Inlines.SHR32(Inlines.MULT16_16(y, y), 2);
                x = pcm[2 * i + 6];
                y = pcm[2 * i + 7];
                pxx += Inlines.SHR32(Inlines.MULT16_16(x, x), 2);
                pxy += Inlines.SHR32(Inlines.MULT16_16(x, y), 2);
                pyy += Inlines.SHR32(Inlines.MULT16_16(y, y), 2);

                xx += Inlines.SHR32(pxx, 10);
                xy += Inlines.SHR32(pxy, 10);
                yy += Inlines.SHR32(pyy, 10);
            }

            mem.XX += Inlines.MULT16_32_Q15(short_alpha, xx - mem.XX);
            mem.XY += Inlines.MULT16_32_Q15(short_alpha, xy - mem.XY);
            mem.YY += Inlines.MULT16_32_Q15(short_alpha, yy - mem.YY);
            mem.XX = Inlines.MAX32(0, mem.XX);
            mem.XY = Inlines.MAX32(0, mem.XY);
            mem.YY = Inlines.MAX32(0, mem.YY);
            if (Inlines.MAX32(mem.XX, mem.YY) > Inlines.QCONST16(8e-4f, 18))
            {
                sqrt_xx = Inlines.celt_sqrt(mem.XX);
                sqrt_yy = Inlines.celt_sqrt(mem.YY);
                qrrt_xx = Inlines.celt_sqrt(sqrt_xx);
                qrrt_yy = Inlines.celt_sqrt(sqrt_yy);
                /* Inter-channel correlation */
                mem.XY = Inlines.MIN32(mem.XY, sqrt_xx * sqrt_yy);
                corr = Inlines.SHR32(Inlines.frac_div32(mem.XY, CeltConstants.EPSILON + Inlines.MULT16_16(sqrt_xx, sqrt_yy)), 16);
                /* Approximate loudness difference */
                ldiff = CeltConstants.Q15ONE * Inlines.ABS16(qrrt_xx - qrrt_yy) / (CeltConstants.EPSILON + qrrt_xx + qrrt_yy);
                width = Inlines.MULT16_16_Q15(Inlines.celt_sqrt(Inlines.QCONST32(1.0f, 30) - Inlines.MULT16_16(corr, corr)), ldiff);
                /* Smoothing over one second */
                mem.smoothed_width += (width - mem.smoothed_width) / frame_rate;
                /* Peak follower */
                mem.max_follower = Inlines.MAX16(mem.max_follower - Inlines.QCONST16(.02f, 15) / frame_rate, mem.smoothed_width);
            }
            else {
                width = 0;
                corr = CeltConstants.Q15ONE;
                ldiff = 0;
            }
            /*printf("%f %f %f %f %f ", corr/(float)1.0f, ldiff/(float)1.0f, width/(float)1.0f, mem.smoothed_width/(float)1.0f, mem.max_follower/(float)1.0f);*/
            return Inlines.EXTRACT16(Inlines.MIN32(CeltConstants.Q15ONE, 20 * mem.max_follower));
        }

        internal static void smooth_fade(Pointer<short> in1, Pointer<short> in2,
              Pointer<short> output, int overlap, int channels,
                Pointer<int> window, int Fs)
        {
            int i, c;
            int inc = 48000 / Fs;
            for (c = 0; c < channels; c++)
            {
                for (i = 0; i < overlap; i++)
                {
                    int w = Inlines.MULT16_16_Q15(window[i * inc], window[i * inc]);
                    output[i * channels + c] = Inlines.CHOP16(Inlines.SHR32(Inlines.MAC16_16(Inlines.MULT16_16(w, in2[i * channels + c]),
                                   CeltConstants.Q15ONE - w, in1[i * channels + c]), 15));
                }
            }
        }

        internal static void opus_pcm_soft_clip(Pointer<float> _x, int N, int C, Pointer<float> declip_mem)
        {
            int c;
            int i;
            Pointer<float> x;

            if (C < 1 || N < 1 || _x == null || declip_mem == null) return;

            /* First thing: saturate everything to +/- 2 which is the highest level our
               non-linearity can handle. At the point where the signal reaches +/-2,
               the derivative will be zero anyway, so this doesn't introduce any
               discontinuity in the derivative. */
            for (i = 0; i < N * C; i++)
                _x[i] = Inlines.MAX16(-2.0f, Inlines.MIN16(2.0f, _x[i]));
            for (c = 0; c < C; c++)
            {
                float a;
                float x0;
                int curr;

                x = _x.Point(c);
                a = declip_mem[c];
                /* Continue applying the non-linearity from the previous frame to avoid
                   any discontinuity. */
                for (i = 0; i < N; i++)
                {
                    if (x[i * C] * a >= 0)
                        break;
                    x[i * C] = x[i * C] + a * x[i * C] * x[i * C];
                }

                curr = 0;
                x0 = x[0];

                while (true)
                {
                    int start, end;
                    float maxval;
                    int special = 0;
                    int peak_pos;
                    for (i = curr; i < N; i++)
                    {
                        if (x[i * C] > 1 || x[i * C] < -1)
                            break;
                    }
                    if (i == N)
                    {
                        a = 0;
                        break;
                    }
                    peak_pos = i;
                    start = end = i;
                    maxval = Inlines.ABS16(x[i * C]);
                    /* Look for first zero crossing before clipping */
                    while (start > 0 && x[i * C] * x[(start - 1) * C] >= 0)
                        start--;
                    /* Look for first zero crossing after clipping */
                    while (end < N && x[i * C] * x[end * C] >= 0)
                    {
                        /* Look for other peaks until the next zero-crossing. */
                        if (Inlines.ABS16(x[end * C]) > maxval)
                        {
                            maxval = Inlines.ABS16(x[end * C]);
                            peak_pos = end;
                        }
                        end++;
                    }
                    /* Detect the special case where we clip before the first zero crossing */
                    special = (start == 0 && x[i * C] * x[0] >= 0) ? 1 : 0;

                    /* Compute a such that maxval + a*maxval^2 = 1 */
                    a = (maxval - 1) / (maxval * maxval);
                    if (x[i * C] > 0)
                        a = -a;
                    /* Apply soft clipping */
                    for (i = start; i < end; i++)
                        x[i * C] = x[i * C] + a * x[i * C] * x[i * C];

                    if (special != 0 && peak_pos >= 2)
                    {
                        /* Add a linear ramp from the first sample to the signal peak.
                           This avoids a discontinuity at the beginning of the frame. */
                        float delta;
                        float offset = x0 - x[0];
                        delta = offset / peak_pos;
                        for (i = curr; i < peak_pos; i++)
                        {
                            offset -= delta;
                            x[i * C] += offset;
                            x[i * C] = Inlines.MAX16(-1.0f, Inlines.MIN16(1.0f, x[i * C]));
                        }
                    }
                    curr = end;
                    if (curr == N)
                    {
                        break;
                    }
                }

                declip_mem[c] = a;
            }
        }


        public static string opus_strerror(int error)
        {
            string[] error_strings = {
              "success",
              "invalid argument",
              "buffer too small",
              "internal error",
              "corrupted stream",
              "request not implemented",
              "invalid state",
              "memory allocation failed"
           };
            if (error > 0 || error < -7)
                return "unknown error";
            else
                return error_strings[-error];
        }

        public static string opus_get_version_string()
        {
            return "concentus 1.0a-fixed"
#if FUZZING
          + "-fuzzing"
#endif
#if PARITY
          + "-parity"
#endif
          ;
        }
    }
}