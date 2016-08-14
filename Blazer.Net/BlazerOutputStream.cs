﻿using System;
using System.IO;

using Force.Blazer.Algorithms;
using Force.Blazer.Algorithms.Crc32C;
using Force.Blazer.Encyption;
using Force.Blazer.Helpers;

namespace Force.Blazer
{
	/// <summary>
	/// Blazer decompression stream implementation
	/// </summary>
	public class BlazerOutputStream : Stream
	{
		#region Stream stub

		/// <summary>
		/// Not supported for this stream
		/// </summary>
		public override void Flush()
		{
			throw new NotSupportedException();
		}

		/// <summary>
		/// Not supported for this stream
		/// </summary>
		public override long Seek(long offset, SeekOrigin origin)
		{
			throw new NotSupportedException();
		}

		/// <summary>
		/// Not supported for this stream
		/// </summary>
		public override void SetLength(long value)
		{
			throw new NotSupportedException();
		}

		/// <summary>
		/// Not supported for this stream
		/// </summary>
		public override void Write(byte[] buffer, int offset, int count)
		{
			throw new NotSupportedException();
		}

		/// <summary>
		/// Returns true
		/// </summary>
		public override bool CanRead
		{
			get
			{
				return true;
			}
		}

		/// <summary>
		/// Not supported for this stream
		/// </summary>
		public override bool CanSeek
		{
			get
			{
				return false;
			}
		}

		/// <summary>
		/// Not supported for this stream
		/// </summary>
		public override bool CanWrite
		{
			get
			{
				return false;
			}
		}

		/// <summary>
		/// Not supported for this stream
		/// </summary>
		public override long Length
		{
			get
			{
				return -1L;
			}
		}

		/// <summary>
		/// Not supported for this stream
		/// </summary>
		public override long Position
		{
			get
			{
				return -1L;
			}

			set
			{
				throw new NotSupportedException();
			}
		}

		#endregion

		private readonly Stream _innerStream;

		private IDecoder _decoder;

		private bool _includeCrc;
		private bool _includeFooter;

		private int _maxUncompressedBlockSize;

		private byte[] _innerBuffer;

		private byte[] _decodedBuffer;

		private int _decodedBufferOffset;

		private int _decodedBufferLength;

		private byte _algorithmId;

		private bool _shouldHaveFileInfo;

		private NullDecryptHelper _decryptHelper;

		private BlazerFileInfo _fileInfo;

		/// <summary>
		/// Returns information about compressed file, if exitsts
		/// </summary>
		public BlazerFileInfo FileInfo
		{
			get
			{
				return _fileInfo;
			}
		}

		private readonly bool _leaveStreamOpen;

		private Action<byte[], int, int> _controlDataCallback { get; set; }

		/// <summary>
		/// Constructs Blazer decompression stream
		/// </summary>
		public BlazerOutputStream(Stream innerStream, BlazerDecompressionOptions options = null)
		{
			options = options ?? BlazerDecompressionOptions.CreateDefault();
			_leaveStreamOpen = options.LeaveStreamOpen;
			_innerStream = innerStream;
			if (!_innerStream.CanRead)
				throw new InvalidOperationException("Base stream is invalid");

			var password = options.Password;
			_controlDataCallback = options.ControlDataCallback ?? ((b, o, c) => { });

			if (options.EncyptFull)
			{
				if (string.IsNullOrEmpty(options.Password))
					throw new InvalidOperationException("Encryption flag was set, but password is missing.");
				_innerStream = DecryptHelper.ConvertStreamToDecyptionStream(innerStream, options.Password);
				// no more password for this
				password = null;
			}

			if (options.CompressionOptions != null)
			{
				var decoder = options.Decoder;
				if (decoder == null)
				{
					if (options.CompressionOptions.Encoder == null)
						throw new InvalidOperationException("Missing decoder information");
					options.SetDecoderByAlgorithm(options.CompressionOptions.Encoder.GetAlgorithmId());
					decoder = options.Decoder;
				}

				InitByFlags(options.CompressionOptions.GetFlags(), decoder, password);
			}
			else
			{
				InitByOptions(password);
			}
		}

		private void InitByFlags(BlazerFlags flags, IDecoder decoder, string password)
		{
			if ((flags & BlazerFlags.IncludeHeader) != 0)
				throw new InvalidOperationException("Flags cannot contains IncludeHeader flags");

			_decoder = decoder;
			_maxUncompressedBlockSize = 1 << ((((int)flags) & 15) + 9);
			_innerBuffer = new byte[_maxUncompressedBlockSize];
			_decoder.Init(_maxUncompressedBlockSize);
			_algorithmId = (byte)_decoder.GetAlgorithmId();
			_includeCrc = (flags & BlazerFlags.IncludeCrc) != 0;
			_includeFooter = (flags & BlazerFlags.IncludeFooter) != 0;
			if (!string.IsNullOrEmpty(password))
			{
				_decryptHelper = new DecryptHelper(password);
				var encHeader = new byte[_decryptHelper.GetHeaderLength()];
				if (!EnsureRead(encHeader, 0, encHeader.Length))
					throw new InvalidOperationException("Missing encryption header");
				_decryptHelper.Init(encHeader, _maxUncompressedBlockSize);
			}
			else
			{
				_decryptHelper = new NullDecryptHelper();
			}
		}

		private void InitByOptions(string password = null)
		{
			if (!_innerStream.CanRead)
				throw new InvalidOperationException("Base stream is invalid");
			_decryptHelper = string.IsNullOrEmpty(password) ? new NullDecryptHelper() : new DecryptHelper(password);

			ReadAndValidateHeader();

			// todo: refactor this
			if (_shouldHaveFileInfo)
			{
				var fInfo = GetNextChunk(_innerBuffer);
				if (_encodingType != 0xfd)
					throw new InvalidOperationException("Invalid file info header");

				_fileInfo = FileHeaderHelper.ParseFileHeader(fInfo.Buffer, fInfo.Offset, fInfo.Count);
			}
		}

		/// <summary>
		/// Releases the unmanaged resources used by the <see cref="T:System.IO.Stream"/> and optionally releases the managed resources.
		/// </summary>
		/// <param name="disposing">true to release both managed and unmanaged resources; false to release only unmanaged resources.</param>
		protected override void Dispose(bool disposing)
		{
			if (!_leaveStreamOpen)
				_innerStream.Dispose();
			_decoder.Dispose();
			base.Dispose(disposing);
		}

		private bool _isFinished;

		/// <summary>
		/// Reads a sequence of bytes from the current stream and advances the position within the stream by the number of bytes read.
		/// </summary>
		/// <returns>
		/// The total number of bytes read into the buffer. This can be less than the number of bytes requested if that many bytes are not currently available, or zero (0) if the end of the stream has been reached.
		/// </returns>
		/// <param name="buffer">An array of bytes. When this method returns, the buffer contains the specified byte array with the values between <paramref name="offset"/> and (<paramref name="offset"/> + <paramref name="count"/> - 1) replaced by the bytes read from the current source. </param>
		/// <param name="offset">The zero-based byte offset in <paramref name="buffer"/> at which to begin storing the data read from the current stream. </param>
		/// <param name="count">The maximum number of bytes to be read from the current stream. </param>
		public override int Read(byte[] buffer, int offset, int count)
		{
			if (_decodedBufferOffset == _decodedBufferLength)
			{
				if (_isFinished) return 0;
				var info = GetNextChunk(_innerBuffer);
				if (info.Count == 0)
				{
					if (_encodingType == 0xf0)
					{
						_controlDataCallback(new byte[0], 0, 0);
						return Read(buffer, offset, count);
					}

					_isFinished = true;
					return 0;
				}

				if (_encodingType != 0 && _encodingType != _algorithmId)
				{
					if (_encodingType == 0xf1)
					{
						_controlDataCallback(info.Buffer, info.Offset, info.Count);
						return Read(buffer, offset, count);
					}

					throw new InvalidOperationException("Invalid header");
				}

				var decoded = _decoder.Decode(info.Buffer, info.Offset, info.Length, _encodingType != 0);
				_decodedBuffer = decoded.Buffer;
				_decodedBufferOffset = decoded.Offset;
				_decodedBufferLength = decoded.Length;
			}

			var toReturn = Math.Min(count, _decodedBufferLength - _decodedBufferOffset);
			Buffer.BlockCopy(_decodedBuffer, _decodedBufferOffset, buffer, offset, toReturn);
			_decodedBufferOffset += toReturn;
			return toReturn;
		}

		private void ReadAndValidateHeader()
		{
			var buf = new byte[8];
			if (!EnsureRead(buf, 0, 8))
				throw new InvalidOperationException("Invalid input stream");
			if (buf[0] != 'b' || buf[1] != 'L' || buf[2] != 'z')
				throw new InvalidOperationException("This is not Blazer archive");
			if (buf[3] != 0x01)
			{
				if (buf[3] > 0x01)
					throw new InvalidOperationException("Stream was created in newer version of Blazer library");
				else
					throw new InvalidOperationException("Stream was created in older version of Blazer library");
			}

			BlazerFlags flags = (BlazerFlags)(buf[4] | ((uint)buf[5] << 8) | ((uint)buf[6] << 16) | ((uint)buf[7] << 24));

			if ((flags & (~BlazerFlags.AllKnownFlags)) != 0)
				throw new InvalidOperationException("Invalid flag combination. Try to use newer version of Blazer");

			_decoder = EncoderDecoderFactory.GetDecoder((BlazerAlgorithm)((((uint)flags) >> 4) & 15));
			_maxUncompressedBlockSize = 1 << ((((int)flags) & 15) + 9);
			_algorithmId = (byte)_decoder.GetAlgorithmId();
			_includeCrc = (flags & BlazerFlags.IncludeCrc) != 0;
			_includeFooter = (flags & BlazerFlags.IncludeFooter) != 0;
			_shouldHaveFileInfo = (flags & BlazerFlags.OnlyOneFile) != 0;
			if ((flags & BlazerFlags.EncryptInner) != 0)
			{
				if (!(_decryptHelper is DecryptHelper)) throw new InvalidOperationException("Stream is encrypted, but password is not provided");
				var encHeader = new byte[_decryptHelper.GetHeaderLength()];
				if (!EnsureRead(encHeader, 0, encHeader.Length)) throw new InvalidOperationException("Missing encryption header");
				_decryptHelper.Init(encHeader, _maxUncompressedBlockSize);
			}
			else
			{
				if (_decryptHelper is DecryptHelper)
					throw new InvalidOperationException("Stream is not encrypted");
			}

			_decoder.Init(_maxUncompressedBlockSize);

			_maxUncompressedBlockSize = _decryptHelper.AdjustLength(_maxUncompressedBlockSize);

			_innerBuffer = new byte[_maxUncompressedBlockSize];

			if (_includeFooter && _innerStream.CanSeek)
			{
				var position = _innerStream.Seek(0, SeekOrigin.Current);
				_innerStream.Seek(-4, SeekOrigin.End);
				if (!EnsureRead(buf, 0, 4))
					throw new Exception("Footer is missing");
				ValidateFooter(buf);
				_innerStream.Seek(position, SeekOrigin.Begin);
			}
		}

// ReSharper disable UnusedParameter.Local
		private void ValidateFooter(byte[] footer)
// ReSharper restore UnusedParameter.Local
		{
			if (footer[0] != 0xff || footer[1] != (byte)'Z' || footer[2] != (byte)'l' || footer[3] != (byte)'B')
				throw new InvalidOperationException("Invalid footer. Possible stream was truncated");
		}

		private readonly byte[] _sizeBlock = new byte[4];

		private byte _encodingType;

		private uint _passedCrc;

		private int GetNextChunkHeader()
		{
			// end of stream
			if (!EnsureRead(_sizeBlock, 0, 4)) return 0;

			_encodingType = _sizeBlock[0];

			// empty footer
			if (_encodingType == 0xff)
			{
				ValidateFooter(_sizeBlock);
				return 0;
			}

			if (_encodingType == 0xf0)
			{
				return 0;
			}

			var inLength = ((_sizeBlock[1] << 0) | (_sizeBlock[2] << 8) | _sizeBlock[3] << 16) + 1;
			if (inLength > _maxUncompressedBlockSize)
				throw new InvalidOperationException("Invalid block size");

			if (_includeCrc)
			{
				if (!EnsureRead(_sizeBlock, 0, 4))
					throw new InvalidOperationException("Missing CRC32C in stream");
				_passedCrc = ((uint)_sizeBlock[0] << 0) | (uint)_sizeBlock[1] << 8 | (uint)_sizeBlock[2] << 16 | (uint)_sizeBlock[3] << 24;
			}

			return inLength;
		}

		private BufferInfo GetNextChunk(byte[] inBuffer)
		{
			var inLength = GetNextChunkHeader();
			if (inLength == 0) return new BufferInfo(null, 0, 0);

			var origInLength = inLength;

			inLength = _decryptHelper.AdjustLength(inLength);

			if (!EnsureRead(inBuffer, 0, inLength))
				throw new InvalidOperationException("Invalid block data");

			var info = _decryptHelper.Decrypt(inBuffer, 0, inLength);

			if (_includeCrc)
			{
				var realCrc = Crc32C.Calculate(inBuffer, 0, inLength);

				if (realCrc != _passedCrc)
					throw new InvalidOperationException("Invalid CRC32C data in passed block. It seems, data error is occured");
			}

			info.Length = origInLength + info.Offset;

			return info;
		}
		
		private bool EnsureRead(byte[] buffer, int offset, int size)
		{
			var sizeOrig = size;
			while (true)
			{
				var cnt = _innerStream.Read(buffer, offset, size);
				if (cnt == 0)
				{
					if (size == sizeOrig) return false;
					throw new InvalidOperationException("Invalid data");
				}

				size -= cnt;
				if (size == 0) return true;
				offset += cnt;
			}
		}
	}
}
