﻿using Concentus.Common.CPlusPlus;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Concentus.Silk.Structs
{
    public class StereoEncodeState
    {
        public readonly Pointer<short> pred_prev_Q13 = Pointer.Malloc<short>(2);
        public readonly Pointer<short> sMid = Pointer.Malloc<short>(2);
        public readonly Pointer<short> sSide = Pointer.Malloc<short>(2);
        public readonly Pointer<int> mid_side_amp_Q0 = Pointer.Malloc<int>(4);
        public short smth_width_Q14 = 0;
        public short width_prev_Q14 = 0;
        public short silent_side_len = 0;
        public readonly Pointer<Pointer<Pointer<sbyte>>> predIx = Arrays.InitThreeDimensionalArrayPointer<sbyte>(SilkConstants.MAX_FRAMES_PER_PACKET, 2, 3);
        public readonly Pointer<sbyte> mid_only_flags = Pointer.Malloc<sbyte>(SilkConstants.MAX_FRAMES_PER_PACKET);
        
        public void Reset()
        {
            pred_prev_Q13.MemSet(0, 2);
            sMid.MemSet(0, 2);
            sSide.MemSet(0, 2);
            mid_side_amp_Q0.MemSet(0, 4);
            smth_width_Q14 = 0;
            width_prev_Q14 = 0;
            silent_side_len = 0;
            for (int x = 0; x < SilkConstants.MAX_FRAMES_PER_PACKET; x++)
            {
                for (int y= 0; y < 2; y++)
                {
                    predIx[x][y].MemSet(0, 3);
                }
            }

            mid_only_flags.MemSet(0, SilkConstants.MAX_FRAMES_PER_PACKET);
        }
    }
}