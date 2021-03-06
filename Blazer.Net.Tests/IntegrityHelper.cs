﻿using System;
using System.IO;

using Force.Blazer;
using Force.Blazer.Algorithms;

using NUnit.Framework;

namespace Blazer.Net.Tests
{
	public static class IntegrityHelper
	{
		public static int CheckCompressDecompress(byte[] inData, BlazerCompressionOptions options, Func<Stream, Stream> decoderCreator = null, int bufferSize = 81920)
		{
			var compressed = CompressData(inData, options, bufferSize);
			var decompressed = DecompressData(compressed, decoderCreator, bufferSize);
			
			CollectionAssert.AreEqual(inData, decompressed);
			return compressed.Length;
		}

		public static byte[] CompressData(byte[] inData, BlazerCompressionOptions options, int bufferSize = 81920)
		{
			var ms1 = new MemoryStream();
			var input = new BlazerInputStream(ms1, options);

			new MemoryStream(inData).CopyTo(input, bufferSize);
			input.Close();
			return ms1.ToArray();
		}

		public static byte[] DecompressData(byte[] inData, Func<Stream, Stream> decoderCreator = null, int bufferSize = 81920)
		{
			var ms3 = new MemoryStream(inData);
			var output = decoderCreator != null ? decoderCreator(ms3) : new BlazerOutputStream(ms3);
			var ms2 = new MemoryStream();
			output.CopyTo(ms2, bufferSize);
			output.Close();
			return ms2.ToArray();
		}

		public static int StreamEncoderCheckCompressDecompress(byte[] inData)
		{
			var compressed = StreamEncoder.CompressData(inData);
			var decompressed = StreamDecoder.DecompressData(compressed);
			CollectionAssert.AreEqual(inData, decompressed);
			return compressed.Length;
		}
	}
}
