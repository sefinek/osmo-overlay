using System.Buffers.Binary;
using System.Security.Cryptography;

namespace OsmoOverlay.Dji.Drones;

/// <summary>
///     The two layers a flight log's records are hidden under: from version 7 an XOR whose key is derived from the record's
///     own first byte and type (no secret), and from version 13 AES-256-CBC on top of it with keys DJI hands out per log.
/// </summary>
internal static class FlightLogCrypto
{
	// CRC-64/Jones, reflected (the one Redis uses), no final xor.
	private const ulong ReflectedPolynomial = 0x95AC9329AC4BC9B5;
	private const ulong XorMagic = 0x123456789ABCDEF0;

	private static readonly ulong[] Table = BuildTable();

	private static ulong[] BuildTable()
	{
		var table = new ulong[256];
		for (int i = 0; i < table.Length; i++)
		{
			ulong crc = (ulong)i;
			for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ ReflectedPolynomial : crc >> 1;
			table[i] = crc;
		}

		return table;
	}

	public static ulong Crc64(ulong crc, ReadOnlySpan<byte> data)
	{
		foreach (byte b in data) crc = Table[(byte)(crc ^ b)] ^ (crc >> 8);
		return crc;
	}

	/// <summary>The 8 bytes a record of `recordType` is xored with, from the record's first byte (which is not part of its content).</summary>
	public static byte[] XorKey(byte firstByte, byte recordType)
	{
		byte[] key = new byte[8];
		WriteXorKey(key, firstByte, recordType);
		return key;
	}

	private static void WriteXorKey(Span<byte> key, byte firstByte, byte recordType)
	{
		Span<byte> seed = stackalloc byte[8];
		BinaryPrimitives.WriteUInt64LittleEndian(seed, unchecked(XorMagic * firstByte));
		BinaryPrimitives.WriteUInt64LittleEndian(key, Crc64((byte)(firstByte + recordType), seed));
	}

	/// <summary>The record's content xored in place; `content` is what follows the record's first byte.</summary>
	public static void Xor(Span<byte> content, byte firstByte, byte recordType)
	{
		Span<byte> key = stackalloc byte[8];
		WriteXorKey(key, firstByte, recordType);
		for (int i = 0; i < content.Length; i++) content[i] ^= key[i & 7];
	}

	/// <summary>
	///     The plaintext of one AES record, with its padding removed - empty when the keys don't fit it. `ciphertext` is the record's
	///     content after the xor; the next record of the same kind continues with its last block as the IV.
	/// </summary>
	public static byte[] AesDecrypt(ReadOnlySpan<byte> ciphertext, byte[] key, byte[] iv)
	{
		if (ciphertext.Length == 0 || ciphertext.Length % 16 != 0) return [];

		using var aes = Aes.Create();
		aes.Key = key;
		// Decrypted without padding and checked by hand: a wrong key or a record the chain has lost is routine here (about a quarter
		// of type 49), and an exception per record is slow and floods the debugger's output.
		byte[] plain = aes.DecryptCbc(ciphertext, iv, PaddingMode.None);
		int padding = plain[^1];
		if (padding is < 1 or > 16) return [];

		for (int i = plain.Length - padding; i < plain.Length; i++)
		{
			if (plain[i] != padding) return [];
		}

		return plain[..^padding];
	}
}
