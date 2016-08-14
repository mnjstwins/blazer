﻿using System;
using System.IO;
using System.Reflection;

using Force.Blazer.Algorithms;

namespace Force.Blazer.Exe
{
	public class Program
	{
		static Program()
		{
#if !DEBUG
			AppDomain.CurrentDomain.AssemblyResolve += CurrentDomainOnAssemblyResolve;
#endif
		}

		public static void Main(string[] args)
		{
			var options = ParseArguments(args);
			if (options == null)
				return;
			if (!options.HasAny())
			{
				Console.Error.WriteLine("Please, specify input file name");
				return;
			}

			try
			{
				Process(options);
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine(ex.Message);
			}
		}

		private static void Process(CommandLineOptions options)
		{
			var isDecompress = options.Has("d", "decompress");
			var isForce = options.Has("f", "force");
			var isStdIn = options.Has("stdin");
			var isStdOut = options.Has("stdout");
			var password = options.Get("p", "password");
			var isBlobOnly = options.Has("blobonly");
			var skipFileName = options.Has("nofilename");
			var encyptFull = options.Has("encryptfull");

			if (isDecompress)
			{
				var archiveName = options.Get("def0") ?? options.Get("d", "decompress") ?? string.Empty;

				if (!isStdIn && !File.Exists(archiveName))
				{
					Console.Error.WriteLine("Archive file " + archiveName + " does not exist");
					return;
				}

				Stream inStreamSource = isStdIn ? Console.OpenStandardInput() : File.OpenRead(archiveName);
				BlazerOutputStream inStream;

				if (!isBlobOnly)
					inStream = new BlazerOutputStream(inStreamSource, new BlazerDecompressionOptions(password));
				else
				{
					var decOptions = new BlazerDecompressionOptions(password) { EncyptFull = encyptFull };
					decOptions.CompressionOptions = new BlazerCompressionOptions
														{
															IncludeCrc = false,
															IncludeFooter = false,
															IncludeHeader = false,
															FileInfo = null,
															MaxBlockSize = 1 << 24
														};

					var mode = (options.Get("mode") ?? "block").ToLowerInvariant();
					if (mode == "stream" || mode == "streamhigh") decOptions.SetDecoderByAlgorithm(BlazerAlgorithm.Stream);
					else if (mode == "none") decOptions.SetDecoderByAlgorithm(BlazerAlgorithm.NoCompress);
					else if (mode == "block") decOptions.SetDecoderByAlgorithm(BlazerAlgorithm.Block);
					else throw new InvalidOperationException("Unsupported mode");
					inStream = new BlazerOutputStream(inStreamSource, decOptions);
				}

				var fileName = archiveName;
				var applyFileInfoAfterComplete = false;
				if (archiveName.EndsWith(".blz")) fileName = fileName.Substring(0, fileName.Length - 4);
				else fileName += ".unpacked";

				if (inStream.FileInfo != null && !skipFileName)
				{
					fileName = inStream.FileInfo.FileName;
					applyFileInfoAfterComplete = true;
				}

				if (!isStdOut && File.Exists(fileName))
				{
					if (!isForce)
					{
						Console.WriteLine("Target " + fileName + " already exists. Overwrite? (Y)es (N)o");
						var readLine = Console.ReadLine();
						if (readLine.Trim().ToLowerInvariant().IndexOf('y') != 0) return;
					}

					new FileStream(fileName, FileMode.Truncate, FileAccess.Write).Close();
				}


				using (var inFile = inStream)
				using (var outFile = isStdOut ? Console.OpenStandardOutput() : new StatStream(new FileStream(fileName, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read), true))
				{
					inFile.CopyTo(outFile);
				}

				if (applyFileInfoAfterComplete) inStream.FileInfo.ApplyToFile();
			}
			else
			{
				var fileName = options.Get("def0") ?? string.Empty;
				if (!isStdIn && !File.Exists(fileName))
				{
					Console.Error.WriteLine("Source file " + fileName + " does not exist");
					return;
				}

				var archiveName = fileName + ".blz";
				if (!isStdOut && File.Exists(archiveName))
				{
					if (!isForce)
					{
						Console.WriteLine("Archive already exists. Overwrite? (Y)es (N)o");
						var readLine = Console.ReadLine();
						if (readLine.Trim().ToLowerInvariant().IndexOf('y') != 0) return;
					}

					new FileStream(archiveName, FileMode.Truncate, FileAccess.Write).Close();
				}

				var mode = (options.Get("mode") ?? "block").ToLowerInvariant();

				// todo: move file opening closer to usage, to ensure we're not removing existing file in case of error
				var outStream = isStdOut ? Console.OpenStandardOutput() : new FileStream(archiveName, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);

				BlazerCompressionOptions compressionOptions = BlazerCompressionOptions.CreateStream();
				compressionOptions.Password = password;
				compressionOptions.EncryptFull = encyptFull;

				if (!skipFileName)
					compressionOptions.FileInfo = BlazerFileInfo.FromFileName(fileName);

				if (mode == "none") 
					compressionOptions.SetEncoderByAlgorithm(BlazerAlgorithm.NoCompress);
				else if (mode == "stream")
					compressionOptions.SetEncoderByAlgorithm(BlazerAlgorithm.Stream);
				else if (mode == "streamhigh")
					compressionOptions.Encoder = new StreamEncoderHigh();
				else if (mode == "block")
				{
					compressionOptions.SetEncoderByAlgorithm(BlazerAlgorithm.Block);
					compressionOptions.MaxBlockSize = BlazerCompressionOptions.DefaultBlockBlockSize;
				}
				else throw new InvalidOperationException("Invalid compression mode");

				if (isBlobOnly)
				{
					compressionOptions.IncludeCrc = false;
					compressionOptions.IncludeFooter = false;
					compressionOptions.IncludeHeader = false;
					compressionOptions.MaxBlockSize = 1 << 24;
					compressionOptions.FileInfo = null;
				}


				Stream blazerStream = new BlazerInputStream(outStream, compressionOptions);

				using (var inFile = isStdIn ? Console.OpenStandardInput() : new StatStream(File.OpenRead(fileName), true))
				using (var outFile = blazerStream)
				{
					inFile.CopyTo(outFile);
				}
			}
		}

		private static Assembly CurrentDomainOnAssemblyResolve(object sender, ResolveEventArgs args)
		{
			if (!args.Name.StartsWith("Blazer.Net")) return null;
			byte[] assemblyBytes;
			using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Force.Blazer.Exe.Resources.Blazer.Net.dll"))
			{
				var ms = new MemoryStream();
				s.CopyTo(ms);
				assemblyBytes = ms.ToArray();
			}

			return Assembly.Load(assemblyBytes);
		}

		private static CommandLineOptions ParseArguments(string[] args)
		{
			try
			{
				return new CommandLineOptions(args);
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine(ex.Message);
				return null;
			}
		}
	}
}