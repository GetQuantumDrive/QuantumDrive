namespace quantum_drive.Helpers;

public static class Base32Encoder
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(byte[] data)
    {
        if (data.Length == 0) return string.Empty;

        var result = new char[(data.Length * 8 + 4) / 5];
        int bitBuffer = data[0];
        int bitsLeft = 8;
        int index = 0;
        int dataIndex = 1;

        while (bitsLeft > 0 || dataIndex < data.Length)
        {
            if (bitsLeft < 5)
            {
                if (dataIndex < data.Length)
                {
                    bitBuffer <<= 8;
                    bitBuffer |= data[dataIndex++] & 0xFF;
                    bitsLeft += 8;
                }
                else
                {
                    int pad = 5 - bitsLeft;
                    bitBuffer <<= pad;
                    bitsLeft += pad;
                }
            }

            bitsLeft -= 5;
            result[index++] = Alphabet[(bitBuffer >> bitsLeft) & 0x1F];
        }

        return new string(result, 0, index);
    }

    public static byte[] Decode(string encoded)
    {
        // Strip dashes and whitespace, uppercase
        var cleaned = new string(encoded.Where(c => c != '-' && !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();

        if (cleaned.Length == 0) return Array.Empty<byte>();

        int byteCount = cleaned.Length * 5 / 8;
        var result = new byte[byteCount];

        int bitBuffer = 0;
        int bitsLeft = 0;
        int index = 0;

        foreach (char c in cleaned)
        {
            int val = Alphabet.IndexOf(c);
            if (val < 0)
                throw new FormatException($"Invalid Base32 character: '{c}'");

            bitBuffer = (bitBuffer << 5) | val;
            bitsLeft += 5;

            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                result[index++] = (byte)(bitBuffer >> bitsLeft);
            }
        }

        return result[..index];
    }

    /// <summary>
    /// Formats a Base32 string with dashes every 4 characters for readability.
    /// Example: "ABCDEFGHIJKLMNOP" -> "ABCD-EFGH-IJKL-MNOP"
    /// </summary>
    public static string FormatWithDashes(string base32, int groupSize = 4)
    {
        if (string.IsNullOrEmpty(base32)) return base32;
        return string.Join("-", Enumerable.Range(0, (base32.Length + groupSize - 1) / groupSize)
            .Select(i => base32.Substring(i * groupSize, Math.Min(groupSize, base32.Length - i * groupSize))));
    }

    /// <summary>
    /// Encodes bytes and formats with dashes for display as a recovery key.
    /// </summary>
    public static string EncodeFormatted(byte[] data)
    {
        return FormatWithDashes(Encode(data));
    }
}
