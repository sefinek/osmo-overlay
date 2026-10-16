using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using OsmoOverlay.Core.Logging;

namespace OsmoOverlay.Core;

/// <summary>
///     Keeps a secret (an API key) unreadable in the files it is stored in. A protected value is "enc1:" plus base64; a value
///     without the prefix is a plain one from before and is returned as it is (and protected the next time it's saved).
///     Windows: DPAPI (CryptProtectData), bound to the user's account. Elsewhere: AES-GCM with a random key in a file only the
///     user can read (0600) next to the settings - it keeps a key out of a settings file that's pasted into a bug report or
///     copied to a backup, not out of reach of whoever already runs as this user (the same limit DPAPI has).
/// </summary>
public static partial class SecretProtector
{
	private const string Prefix = "enc1:";
	private const int KeySize = 32, NonceSize = 12, TagSize = 16;

	private static readonly byte[] Entropy = "OsmoOverlay.Secrets.v1"u8.ToArray();

	private static readonly Lock KeyLock = new();
	private static byte[]? _key;

	public static bool IsProtected(string value)
	{
		return value.StartsWith(Prefix, StringComparison.Ordinal);
	}

	/// <summary>The value ready to be written: protected, or as it is when it already is or the platform can't protect it.</summary>
	public static string Protect(string value)
	{
		if (value.Length == 0 || IsProtected(value)) return value;

		try
		{
			byte[] plain = Encoding.UTF8.GetBytes(value);
			byte[] protectedBytes = OperatingSystem.IsWindows() ? ProtectWindows(plain) : ProtectAes(plain);
			return Prefix + Convert.ToBase64String(protectedBytes);
		}
		catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
		{
			AppLogger.Warn(ex, "A secret could not be protected - it is stored as it is.");
			return value;
		}
	}

	/// <summary>The value as it was before Protect; a plain one as it is; null when it can't be read here (another account or machine, damaged).</summary>
	public static string? Unprotect(string value)
	{
		if (!IsProtected(value)) return value;

		try
		{
			byte[] data = Convert.FromBase64String(value[Prefix.Length..]);
			byte[] plain = OperatingSystem.IsWindows() ? UnprotectWindows(data) : UnprotectAes(data);
			return Encoding.UTF8.GetString(plain);
		}
		catch (Exception ex) when (ex is CryptographicException or FormatException or IOException or UnauthorizedAccessException
		                               or PlatformNotSupportedException)
		{
			AppLogger.Warn(ex, "A stored secret could not be read on this account or computer - it is treated as not set.");
			return null;
		}
	}

	private static byte[] ProtectAes(byte[] plain)
	{
		if (!AesGcm.IsSupported) throw new PlatformNotSupportedException("AES-GCM is not available on this system.");

		byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
		byte[] cipher = new byte[plain.Length], tag = new byte[TagSize];
		using var aes = new AesGcm(Key(), TagSize);
		aes.Encrypt(nonce, plain, cipher, tag, Entropy);
		return [.. nonce, .. tag, .. cipher];
	}

	private static byte[] UnprotectAes(byte[] data)
	{
		if (data.Length < NonceSize + TagSize) throw new CryptographicException("The protected value is too short.");

		ReadOnlySpan<byte> all = data;
		byte[] plain = new byte[data.Length - NonceSize - TagSize];
		using var aes = new AesGcm(Key(), TagSize);
		aes.Decrypt(all[..NonceSize], all[(NonceSize + TagSize)..], all[NonceSize..(NonceSize + TagSize)], plain, Entropy);
		return plain;
	}

	private static string KeyPath => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OsmoOverlay", "secret.key");

	private static byte[] Key()
	{
		lock (KeyLock)
		{
			if (_key is not null) return _key;

			string path = KeyPath;
			if (File.Exists(path))
			{
				byte[] existing = File.ReadAllBytes(path);
				if (existing.Length != KeySize) throw new CryptographicException("The key file is damaged.");
				return _key = existing;
			}

			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			byte[] created = RandomNumberGenerator.GetBytes(KeySize);
			var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
			if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
			try
			{
				using var stream = new FileStream(path, options);
				stream.Write(created);
			}
			catch (IOException) when (File.Exists(path))
			{
				// Another process (the CLI next to the GUI) made it first - its key is the one that counts.
				return _key = File.ReadAllBytes(path);
			}

			return _key = created;
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct DataBlob
	{
		public uint Size;
		public nint Data;
	}

	private const uint CryptProtectUiForbidden = 0x1;

	[LibraryImport("crypt32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool CryptProtectData(ref DataBlob dataIn, nint description, ref DataBlob entropy, nint reserved, nint prompt,
		uint flags, out DataBlob dataOut);

	[LibraryImport("crypt32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool CryptUnprotectData(ref DataBlob dataIn, nint description, ref DataBlob entropy, nint reserved, nint prompt,
		uint flags, out DataBlob dataOut);

	[LibraryImport("kernel32.dll")]
	private static partial nint LocalFree(nint memory);

	private delegate bool DpapiCall(ref DataBlob input, ref DataBlob entropy, out DataBlob output);

	private static byte[] ProtectWindows(byte[] plain)
	{
		return Dpapi(plain, static (ref DataBlob input, ref DataBlob entropy, out DataBlob output) =>
			CryptProtectData(ref input, 0, ref entropy, 0, 0, CryptProtectUiForbidden, out output));
	}

	private static byte[] UnprotectWindows(byte[] data)
	{
		return Dpapi(data, static (ref DataBlob input, ref DataBlob entropy, out DataBlob output) =>
			CryptUnprotectData(ref input, 0, ref entropy, 0, 0, CryptProtectUiForbidden, out output));
	}

	private static unsafe byte[] Dpapi(byte[] input, DpapiCall call)
	{
		fixed (byte* inputPointer = input)
		fixed (byte* entropyPointer = Entropy)
		{
			var inBlob = new DataBlob { Size = (uint)input.Length, Data = (nint)inputPointer };
			var entropyBlob = new DataBlob { Size = (uint)Entropy.Length, Data = (nint)entropyPointer };
			if (!call(ref inBlob, ref entropyBlob, out DataBlob outBlob))
				throw new CryptographicException(Marshal.GetLastPInvokeError());

			try
			{
				byte[] result = new byte[outBlob.Size];
				Marshal.Copy(outBlob.Data, result, 0, result.Length);
				return result;
			}
			finally
			{
				LocalFree(outBlob.Data);
			}
		}
	}
}
