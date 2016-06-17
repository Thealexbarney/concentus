﻿using Concentus.Common.CPlusPlus;
using Concentus.Common;
using Concentus.Silk.Enums;
using Concentus.Silk.Structs;
using System.Diagnostics;

namespace Concentus.Silk
{
    internal static class LTPScaleControl
    {
        /* Calculation of LTP state scaling */
        internal static void silk_LTP_scale_ctrl(
            SilkChannelEncoder psEnc,                                 /* I/O  encoder state                                                               */
            SilkEncoderControl psEncCtrl,                             /* I/O  encoder control                                                             */
            int condCoding                              /* I    The type of conditional coding to use                                       */
        )
        {
            int round_loss;

            if (condCoding == SilkConstants.CODE_INDEPENDENTLY)
            {
                /* Only scale if first frame in packet */
                round_loss = psEnc.PacketLoss_perc + psEnc.nFramesPerPacket;
                psEnc.indices.LTP_scaleIndex = (sbyte)Inlines.silk_LIMIT(
                    Inlines.silk_SMULWB(Inlines.silk_SMULBB(round_loss, psEncCtrl.LTPredCodGain_Q7), Inlines.SILK_CONST(0.1f, 9)), 0, 2);
            }
            else {
                /* Default is minimum scaling */
                psEnc.indices.LTP_scaleIndex = 0;
            }
            psEncCtrl.LTP_scale_Q14 = Tables.silk_LTPScales_table_Q14[psEnc.indices.LTP_scaleIndex];
        }
    }
}