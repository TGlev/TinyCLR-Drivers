/*
* Copyright 2008 ZXing authors
*
* Licensed under the Apache License, Version 2.0 (the "License");
* you may not use this file except in compliance with the License.
* You may obtain a copy of the License at
*
*      http://www.apache.org/licenses/LICENSE-2.0
*
* Unless required by applicable law or agreed to in writing, software
* distributed under the License is distributed on an "AS IS" BASIS,
* WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
* See the License for the specific language governing permissions and
* limitations under the License.
*/

using System;
using System.Collections;
using System.Text;
//using Microsoft.SPOT;
using GHIElectronics.TinyCLR.Drivers.Barcode.Common;
using GHIElectronics.TinyCLR.Drivers.Barcode.Common.ReedSolomon;
using GHIElectronics.TinyCLR.Drivers.Barcode.QrCode.Internal;
using Math = System.Math;

namespace GHIElectronics.TinyCLR.Drivers.Barcode.QrCode.Internal
{
   /// <author>  satorux@google.com (Satoru Takabayashi) - creator
   /// </author>
   /// <author>  dswitkin@google.com (Daniel Switkin) - ported from C++
   /// </author>
   /// <author>www.Redivivus.in (suraj.supekar@redivivus.in) - Ported from ZXING Java Source 
   /// </author>
   public static class Encoder
   {

      // The original table is defined in the table 5 of JISX0510:2004 (p.19).
      private static readonly int[] ALPHANUMERIC_TABLE = {
         -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,  // 0x00-0x0f
         -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1,  // 0x10-0x1f
         36, -1, -1, -1, 37, 38, -1, -1, -1, -1, 39, 40, -1, 41, 42, 43,  // 0x20-0x2f
         0,   1,  2,  3,  4,  5,  6,  7,  8,  9, 44, -1, -1, -1, -1, -1,  // 0x30-0x3f
         -1, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24,  // 0x40-0x4f
         25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, -1, -1, -1, -1, -1,  // 0x50-0x5f
      };

      internal static string DEFAULT_BYTE_MODE_ENCODING = "ISO-8859-1";

      // The mask penalty calculation is complicated.  See Table 21 of JISX0510:2004 (p.45) for details.
      // Basically it applies four rules and summate all penalties.
      private static int calculateMaskPenalty(ByteMatrix matrix)
      {
         return MaskUtil.applyMaskPenaltyRule1(matrix)
                 + MaskUtil.applyMaskPenaltyRule2(matrix)
                 + MaskUtil.applyMaskPenaltyRule3(matrix)
                 + MaskUtil.applyMaskPenaltyRule4(matrix);
      }

      /// <summary>
      /// Encode "bytes" with the error correction level "ecLevel". The encoding mode will be chosen
      /// internally by chooseMode(). On success, store the result in "qrCode".
      /// We recommend you to use QRCode.EC_LEVEL_L (the lowest level) for
      /// "getECLevel" since our primary use is to show QR code on desktop screens. We don't need very
      /// strong error correction for this purpose.
      /// Note that there is no way to encode bytes in MODE_KANJI. We might want to add EncodeWithMode()
      /// with which clients can specify the encoding mode. For now, we don't need the functionality.
      /// </summary>
      /// <param name="content">The content.</param>
      /// <param name="ecLevel">The ec level.</param>
      /// <param name="qrCode">The qr code.</param>
      public static QRCode encode(string content, ErrorCorrectionLevel ecLevel)
      {
         return encode(content, ecLevel, null);
      }

      public static QRCode encode(string content,
                                ErrorCorrectionLevel ecLevel,
                                IDictionary hints)
      {
            // Determine what character encoding has been specified by the caller, if any
            string encoding = hints == null || !hints.Contains(EncodeHintType.CHARACTER_SET) ? null : (string)hints[EncodeHintType.CHARACTER_SET];
         if (encoding == null)
         {
            encoding = DEFAULT_BYTE_MODE_ENCODING;
         }
         bool doSelectMask = hints == null || !hints.Contains(EncodeHintType.QR_DO_MASK_SELECTION) ? false : (bool)hints[EncodeHintType.QR_DO_MASK_SELECTION];

         // Pick an encoding mode appropriate for the content. Note that this will not attempt to use
         // multiple modes / segments even if that were more efficient. Twould be nice.
         Mode mode = chooseMode(content, encoding);

         // This will store the header information, like mode and
         // length, as well as "header" segments like an ECI segment.
         BitArray headerBits = new BitArray();

         // Append ECI segment if applicable
         if (mode == Mode.BYTE && !DEFAULT_BYTE_MODE_ENCODING.Equals(encoding))
         {
            CharacterSetECI eci = CharacterSetECI.getCharacterSetECIByName(encoding);
            if (eci != null)
            {
               appendECI(eci, headerBits);
            }
         }

         // (With ECI in place,) Write the mode marker
         appendModeInfo(mode, headerBits);

         // Collect data within the main segment, separately, to count its size if needed. Don't add it to
         // main payload yet.
         BitArray dataBits = new BitArray();
         appendBytes(content, mode, dataBits, encoding);

         // Hard part: need to know version to know how many bits length takes. But need to know how many
         // bits it takes to know version. To pick version size assume length takes maximum bits
         int bitsNeeded = headerBits.Size
             + mode.getCharacterCountBits(Version.getVersionForNumber(40))
             + dataBits.Size;
         Version version = chooseVersion(bitsNeeded, ecLevel);

         BitArray headerAndDataBits = new BitArray();
         headerAndDataBits.appendBitArray(headerBits);
         // Find "length" of main segment and write it
         int numLetters = mode == Mode.BYTE ? dataBits.SizeInBytes : content.Length;
         appendLengthInfo(numLetters, version, mode, headerAndDataBits);
         // Put data together into the overall payload
         headerAndDataBits.appendBitArray(dataBits);

         Version.ECBlocks ecBlocks = version.getECBlocksForLevel(ecLevel);
         int numDataBytes = version.TotalCodewords - ecBlocks.TotalECCodewords;

         // Terminate the bits properly.
         terminateBits(numDataBytes, headerAndDataBits);

         // Interleave data bits with error correction code.
         BitArray finalBits = interleaveWithECBytes(headerAndDataBits,
                                                    version.TotalCodewords,
                                                    numDataBytes,
                                                    ecBlocks.NumBlocks);

         QRCode qrCode = new QRCode
                            {
                               ECLevel = ecLevel, 
                               Mode = mode,
                               Version = version
                            };

         //  Choose the mask pattern and set to "qrCode".
         int dimension = version.DimensionForVersion;
         ByteMatrix matrix = new ByteMatrix(dimension, dimension);
         int maskPattern = 3;
         if (doSelectMask)
            maskPattern = chooseMaskPattern(finalBits, ecLevel, version, matrix);
         qrCode.MaskPattern = maskPattern;

         // Build the matrix and set it to "qrCode".
         MatrixUtil.buildMatrix(finalBits, ecLevel, version, maskPattern, matrix);
         qrCode.Matrix = matrix;

         return qrCode;
      }

      /**
       * @return the code point of the table used in alphanumeric mode or
       *  -1 if there is no corresponding code in the table.
       */

      internal static int getAlphanumericCode(int code)
      {
         if (code < ALPHANUMERIC_TABLE.Length)
         {
            return ALPHANUMERIC_TABLE[code];
         }
         return -1;
      }

      public static Mode chooseMode(string content)
      {
         return chooseMode(content, null);
      }

      /**
       * Choose the best mode by examining the content. Note that 'encoding' is used as a hint;
       * if it is Shift_JIS, and the input is only double-byte Kanji, then we return {@link Mode#KANJI}.
       */
      private static Mode chooseMode(string content, string encoding)
      {
         if ("Shift_JIS".Equals(encoding))
         {

            // Choose Kanji mode if all input are double-byte characters
            return isOnlyDoubleByteKanji(content) ? Mode.KANJI : Mode.BYTE;
         }
         bool hasNumeric = false;
         bool hasAlphanumeric = false;
         for (int i = 0; i < content.Length; ++i)
         {
            char c = content[i];
            if (c >= '0' && c <= '9')
            {
               hasNumeric = true;
            }
            else if (getAlphanumericCode(c) != -1)
            {
               hasAlphanumeric = true;
            }
            else
            {
               return Mode.BYTE;
            }
         }
         if (hasAlphanumeric)
         {

            return Mode.ALPHANUMERIC;
         }
         if (hasNumeric)
         {

            return Mode.NUMERIC;
         }
         return Mode.BYTE;
      }

      private static bool isOnlyDoubleByteKanji(string content)
      {
         byte[] bytes;
         try
         {
            bytes = Encoding.UTF8.GetBytes(content);
         }
         catch (Exception )
         {
            return false;
         }
         int length = bytes.Length;
         if (length % 2 != 0)
         {
            return false;
         }
         for (int i = 0; i < length; i += 2)
         {


            int byte1 = bytes[i] & 0xFF;
            if ((byte1 < 0x81 || byte1 > 0x9F) && (byte1 < 0xE0 || byte1 > 0xEB))
            {

               return false;
            }
         }
         return true;
      }

      private static int chooseMaskPattern(BitArray bits,
                                           ErrorCorrectionLevel ecLevel,
                                           Version version,
                                           ByteMatrix matrix)
      {
         int minPenalty = int.MaxValue;  // Lower penalty is better.
         int bestMaskPattern = -1;
         // We try all mask patterns to choose the best one.
         for (int maskPattern = 0; maskPattern < QRCode.NUM_MASK_PATTERNS; maskPattern++)
         {

            MatrixUtil.buildMatrix(bits, ecLevel, version, maskPattern, matrix);
            int penalty = calculateMaskPenalty(matrix);
            if (penalty < minPenalty)
            {

               minPenalty = penalty;
               bestMaskPattern = maskPattern;
            }
         }
         return bestMaskPattern;
      }

      private static Version chooseVersion(int numInputBits, ErrorCorrectionLevel ecLevel)
      {
         // In the following comments, we use numbers of Version 7-H.
         for (int versionNum = 1; versionNum <= 40; versionNum++)
         {
            Version version = Version.getVersionForNumber(versionNum);
            // numBytes = 196
            int numBytes = version.TotalCodewords;
            // getNumECBytes = 130
            Version.ECBlocks ecBlocks = version.getECBlocksForLevel(ecLevel);
            int numEcBytes = ecBlocks.TotalECCodewords;
            // getNumDataBytes = 196 - 130 = 66
            int numDataBytes = numBytes - numEcBytes;
            int totalInputBytes = (numInputBits + 7) / 8;
            if (numDataBytes >= totalInputBytes)
            {
               return version;
            }
         }
         throw new WriterException("Data too big");
      }

      /**
       * Terminate bits as described in 8.4.8 and 8.4.9 of JISX0510:2004 (p.24).
       */

      internal static void terminateBits(int numDataBytes, BitArray bits)
      {
         int capacity = numDataBytes << 3;
         if (bits.Size > capacity)
         {
            throw new WriterException("data bits cannot fit in the QR Code" + bits.Size + " > " +
                capacity);
         }
         for (int i = 0; i < 4 && bits.Size < capacity; ++i)
         {
            bits.appendBit(false);
         }
         // Append termination bits. See 8.4.8 of JISX0510:2004 (p.24) for details.
         // If the last byte isn't 8-bit aligned, we'll add padding bits.
         int numBitsInLastByte = bits.Size & 0x07;
         if (numBitsInLastByte > 0)
         {
            for (int i = numBitsInLastByte; i < 8; i++)
            {
               bits.appendBit(false);
            }
         }
         // If we have more space, we'll fill the space with padding patterns defined in 8.4.9 (p.24).
         int numPaddingBytes = numDataBytes - bits.SizeInBytes;
         for (int i = 0; i < numPaddingBytes; ++i)
         {
            bits.appendBits((i & 0x01) == 0 ? 0xEC : 0x11, 8);
         }
         if (bits.Size != capacity)
         {
            throw new WriterException("Bits size does not equal capacity");
         }
      }

      /**
       * Get number of data bytes and number of error correction bytes for block id "blockID". Store
       * the result in "numDataBytesInBlock", and "numECBytesInBlock". See table 12 in 8.5.1 of
       * JISX0510:2004 (p.30)
       */

      internal static void getNumDataBytesAndNumECBytesForBlockID(int numTotalBytes,
                                                         int numDataBytes,
                                                         int numRSBlocks,
                                                         int blockID,
                                                         int[] numDataBytesInBlock,
                                                         int[] numECBytesInBlock)
      {
         if (blockID >= numRSBlocks)
         {
            throw new WriterException("Block ID too large");
         }
         // numRsBlocksInGroup2 = 196 % 5 = 1
         int numRsBlocksInGroup2 = numTotalBytes % numRSBlocks;
         // numRsBlocksInGroup1 = 5 - 1 = 4
         int numRsBlocksInGroup1 = numRSBlocks - numRsBlocksInGroup2;
         // numTotalBytesInGroup1 = 196 / 5 = 39
         int numTotalBytesInGroup1 = numTotalBytes / numRSBlocks;
         // numTotalBytesInGroup2 = 39 + 1 = 40
         int numTotalBytesInGroup2 = numTotalBytesInGroup1 + 1;
         // numDataBytesInGroup1 = 66 / 5 = 13
         int numDataBytesInGroup1 = numDataBytes / numRSBlocks;
         // numDataBytesInGroup2 = 13 + 1 = 14
         int numDataBytesInGroup2 = numDataBytesInGroup1 + 1;
         // numEcBytesInGroup1 = 39 - 13 = 26
         int numEcBytesInGroup1 = numTotalBytesInGroup1 - numDataBytesInGroup1;
         // numEcBytesInGroup2 = 40 - 14 = 26
         int numEcBytesInGroup2 = numTotalBytesInGroup2 - numDataBytesInGroup2;
         // Sanity checks.
         // 26 = 26
         if (numEcBytesInGroup1 != numEcBytesInGroup2)
         {

            throw new WriterException("EC bytes mismatch");
         }
         // 5 = 4 + 1.
         if (numRSBlocks != numRsBlocksInGroup1 + numRsBlocksInGroup2)
         {

            throw new WriterException("RS blocks mismatch");
         }
         // 196 = (13 + 26) * 4 + (14 + 26) * 1
         if (numTotalBytes !=
             ((numDataBytesInGroup1 + numEcBytesInGroup1) *
                 numRsBlocksInGroup1) +
                 ((numDataBytesInGroup2 + numEcBytesInGroup2) *
                     numRsBlocksInGroup2))
         {
            throw new WriterException("Total bytes mismatch");
         }

         if (blockID < numRsBlocksInGroup1)
         {

            numDataBytesInBlock[0] = numDataBytesInGroup1;
            numECBytesInBlock[0] = numEcBytesInGroup1;
         }
         else
         {


            numDataBytesInBlock[0] = numDataBytesInGroup2;
            numECBytesInBlock[0] = numEcBytesInGroup2;
         }
      }

      /// <summary>
      /// Interleave "bits" with corresponding error correction bytes. On success, store the result in
      /// "result". The interleave rule is complicated. See 8.6 of JISX0510:2004 (p.37) for details.
      /// </summary>
      /// <param name="bits">The bits.</param>
      /// <param name="numTotalBytes">The num total bytes.</param>
      /// <param name="numDataBytes">The num data bytes.</param>
      /// <param name="numRSBlocks">The num RS blocks.</param>
      /// <returns></returns>
      internal static BitArray interleaveWithECBytes(BitArray bits,
                                             int numTotalBytes,
                                             int numDataBytes,
                                             int numRSBlocks)
      {
         // "bits" must have "getNumDataBytes" bytes of data.
         if (bits.SizeInBytes != numDataBytes)
         {

            throw new WriterException("Number of bits and data bytes does not match");
         }

         // Step 1.  Divide data bytes into blocks and generate error correction bytes for them. We'll
         // store the divided data bytes blocks and error correction bytes blocks into "blocks".
         int dataBytesOffset = 0;
         int maxNumDataBytes = 0;
         int maxNumEcBytes = 0;

         // Since, we know the number of reedsolmon blocks, we can initialize the vector with the number.
         var blocks = new ArrayList();

         for (int i = 0; i < numRSBlocks; ++i)
         {

            int[] numDataBytesInBlock = new int[1];
            int[] numEcBytesInBlock = new int[1];
            getNumDataBytesAndNumECBytesForBlockID(
                numTotalBytes, numDataBytes, numRSBlocks, i,
                numDataBytesInBlock, numEcBytesInBlock);

            int size = numDataBytesInBlock[0];
            byte[] dataBytes = new byte[size];
            bits.toBytes(8 * dataBytesOffset, dataBytes, 0, size);
            byte[] ecBytes = generateECBytes(dataBytes, numEcBytesInBlock[0]);
            blocks.Add(new BlockPair(dataBytes, ecBytes));

            maxNumDataBytes = Math.Max(maxNumDataBytes, size);
            maxNumEcBytes = Math.Max(maxNumEcBytes, ecBytes.Length);
            dataBytesOffset += numDataBytesInBlock[0];
         }
         if (numDataBytes != dataBytesOffset)
         {

            throw new WriterException("Data bytes does not match offset");
         }

         BitArray result = new BitArray();

         // First, place data blocks.
         for (int i = 0; i < maxNumDataBytes; ++i)
         {
            foreach (BlockPair block in blocks)
            {
               byte[] dataBytes = block.DataBytes;
               if (i < dataBytes.Length)
               {
                  result.appendBits(dataBytes[i], 8);
               }
            }
         }
         // Then, place error correction blocks.
         for (int i = 0; i < maxNumEcBytes; ++i)
         {
            foreach (BlockPair block in blocks)
            {
               byte[] ecBytes = block.ErrorCorrectionBytes;
               if (i < ecBytes.Length)
               {
                  result.appendBits(ecBytes[i], 8);
               }
            }
         }
         if (numTotalBytes != result.SizeInBytes)
         {  // Should be same.
            throw new WriterException("Interleaving error: " + numTotalBytes + " and " +
                result.SizeInBytes + " differ.");
         }

         return result;
      }

      internal static byte[] generateECBytes(byte[] dataBytes, int numEcBytesInBlock)
      {
         int numDataBytes = dataBytes.Length;
         int[] toEncode = new int[numDataBytes + numEcBytesInBlock];
         for (int i = 0; i < numDataBytes; i++)
         {
            toEncode[i] = dataBytes[i] & 0xFF;

         }
         new ReedSolomonEncoder(GenericGF.QR_CODE_FIELD_256).encode(toEncode, numEcBytesInBlock);

         byte[] ecBytes = new byte[numEcBytesInBlock];
         for (int i = 0; i < numEcBytesInBlock; i++)
         {
            ecBytes[i] = (byte)toEncode[numDataBytes + i];

         }
         return ecBytes;
      }

      /// <summary>
      /// Append mode info. On success, store the result in "bits".
      /// </summary>
      /// <param name="mode">The mode.</param>
      /// <param name="bits">The bits.</param>
      internal static void appendModeInfo(Mode mode, BitArray bits)
      {
         bits.appendBits(mode.Bits, 4);
      }


      /// <summary>
      /// Append length info. On success, store the result in "bits".
      /// </summary>
      /// <param name="numLetters">The num letters.</param>
      /// <param name="version">The version.</param>
      /// <param name="mode">The mode.</param>
      /// <param name="bits">The bits.</param>
      internal static void appendLengthInfo(int numLetters, Version version, Mode mode, BitArray bits)
      {
         int numBits = mode.getCharacterCountBits(version);
         if (numLetters >= (1 << numBits))
         {
            throw new WriterException(numLetters + " is bigger than " + ((1 << numBits) - 1));
         }
         bits.appendBits(numLetters, numBits);
      }

      /// <summary>
      /// Append "bytes" in "mode" mode (encoding) into "bits". On success, store the result in "bits".
      /// </summary>
      /// <param name="content">The content.</param>
      /// <param name="mode">The mode.</param>
      /// <param name="bits">The bits.</param>
      /// <param name="encoding">The encoding.</param>
      internal static void appendBytes(string content,
                              Mode mode,
                              BitArray bits,
                              string encoding)
      {
         if (mode.Equals(Mode.NUMERIC))
            appendNumericBytes(content, bits);
         else
            if (mode.Equals(Mode.ALPHANUMERIC))
               appendAlphanumericBytes(content, bits);
            else
               if (mode.Equals(Mode.BYTE))
                  append8BitBytes(content, bits, encoding);
               else
                  if (mode.Equals(Mode.KANJI))
                     appendKanjiBytes(content, bits);
                  else
                     throw new WriterException("Invalid mode: " + mode);
      }

      internal static void appendNumericBytes(string content, BitArray bits)
      {
         int length = content.Length;

         int i = 0;
         while (i < length)
         {
            int num1 = content[i] - '0';
            if (i + 2 < length)
            {
               // Encode three numeric letters in ten bits.
               int num2 = content[i + 1] - '0';
               int num3 = content[i + 2] - '0';
               bits.appendBits(num1 * 100 + num2 * 10 + num3, 10);
               i += 3;
            }
            else if (i + 1 < length)
            {
               // Encode two numeric letters in seven bits.
               int num2 = content[i + 1] - '0';
               bits.appendBits(num1 * 10 + num2, 7);
               i += 2;
            }
            else
            {
               // Encode one numeric letter in four bits.
               bits.appendBits(num1, 4);
               i++;
            }
         }
      }

      internal static void appendAlphanumericBytes(string content, BitArray bits)
      {
         int length = content.Length;

         int i = 0;
         while (i < length)
         {
            int code1 = getAlphanumericCode(content[i]);
            if (code1 == -1)
            {
               throw new WriterException();
            }
            if (i + 1 < length)
            {
               int code2 = getAlphanumericCode(content[i + 1]);
               if (code2 == -1)
               {
                  throw new WriterException();
               }
               // Encode two alphanumeric letters in 11 bits.
               bits.appendBits(code1 * 45 + code2, 11);
               i += 2;
            }
            else
            {
               // Encode one alphanumeric letter in six bits.
               bits.appendBits(code1, 6);
               i++;
            }
         }
      }

      internal static void append8BitBytes(string content, BitArray bits, string encoding)
      {
         byte[] bytes;
         try
         {
            bytes = Encoding.UTF8.GetBytes(content);
         }
         catch (Exception uee)
         {
            throw new WriterException(uee.Message, uee);
         }
         foreach (byte b in bytes)
         {
            bits.appendBits(b, 8);
         }
      }

      internal static void appendKanjiBytes(string content, BitArray bits)
      {
         byte[] bytes;
         try
         {
            bytes = Encoding.UTF8.GetBytes(content);
         }
         catch (Exception uee)
         {
            throw new WriterException(uee.Message, uee);
         }
         int length = bytes.Length;
         for (int i = 0; i < length; i += 2)
         {
            int byte1 = bytes[i] & 0xFF;
            int byte2 = bytes[i + 1] & 0xFF;
            int code = (byte1 << 8) | byte2;
            int subtracted = -1;
            if (code >= 0x8140 && code <= 0x9ffc)
            {

               subtracted = code - 0x8140;
            }
            else if (code >= 0xe040 && code <= 0xebbf)
            {
               subtracted = code - 0xc140;
            }
            if (subtracted == -1)
            {

               throw new WriterException("Invalid byte sequence");
            }
            int encoded = ((subtracted >> 8) * 0xc0) + (subtracted & 0xff);
            bits.appendBits(encoded, 13);
         }
      }

      private static void appendECI(CharacterSetECI eci, BitArray bits)
      {
         bits.appendBits(Mode.ECI.Bits, 4);

         // This is correct for values up to 127, which is all we need now.
         bits.appendBits(eci.Value, 8);
      }
   }
}
