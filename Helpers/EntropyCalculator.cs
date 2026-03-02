using System;
using System.Linq;

namespace quantum_drive.Helpers;

public static class EntropyCalculator
{
    // Common passwords / fragments that should penalize the score heavily.
    // Kept lowercase — we compare against password.ToLowerInvariant().
    private static readonly string[] CommonPasswords =
    [
        "password", "123456", "12345678", "qwerty", "abc123", "letmein",
        "monkey", "dragon", "master", "login", "welcome", "shadow",
        "sunshine", "princess", "trustno1", "iloveyou", "admin", "football",
        "passw0rd", "p@ssword", "p@ssw0rd", "quantum", "quantumdrive"
    ];

    public static double CalculateBits(string password)
    {
        if (string.IsNullOrEmpty(password)) return 0;

        // 1. Base entropy from character-class pool size
        int poolSize = 0;
        if (password.Any(char.IsLower)) poolSize += 26;
        if (password.Any(char.IsUpper)) poolSize += 26;
        if (password.Any(char.IsDigit)) poolSize += 10;
        if (password.Any(c => !char.IsLetterOrDigit(c))) poolSize += 32;
        if (poolSize == 0) return 0;

        double bits = password.Length * Math.Log2(poolSize);

        // 2. Penalty: repeated characters (e.g., "aaaa" or "1111")
        double repeatRatio = 1.0 - (double)password.Distinct().Count() / password.Length;
        if (repeatRatio > 0.3)
            bits *= 1.0 - (repeatRatio * 0.5); // up to 50% penalty for very repetitive passwords

        // 3. Penalty: sequential runs (abc, 123, qwerty rows)
        int sequentialRuns = CountSequentialRuns(password);
        if (sequentialRuns > 0)
            bits *= Math.Max(0.4, 1.0 - sequentialRuns * 0.15);

        // 4. Penalty: common passwords / dictionary words
        string lower = password.ToLowerInvariant();
        foreach (var common in CommonPasswords)
        {
            if (lower.Contains(common))
            {
                bits *= 0.3; // 70% penalty for containing a common password
                break;
            }
        }

        // 5. Penalty: all same character class (e.g., all digits, all lowercase)
        if (password.All(char.IsDigit) || password.All(char.IsLower) || password.All(char.IsUpper))
            bits *= 0.6;

        return Math.Max(0, bits);
    }

    public static string GetStrengthLabel(double bits)
    {
        return bits switch
        {
            < 28 => "Very Weak (Instantly crackable)",
            < 50 => "Weak (Days to crack)",
            < 60 => "Moderate (Years to crack)",
            < 80 => "Strong (Centuries to crack)",
            _ => "Exceptional (Eons to crack)"
        };
    }

    public static string GetTimeToCrack(double bits)
    {
        // Assume 1 billion guesses/sec (fast offline attack)
        double seconds = Math.Pow(2, bits) / 1_000_000_000;

        if (seconds < 1) return "Instantly";
        if (seconds < 60) return $"{seconds:F0} seconds";
        if (seconds < 3600) return $"{seconds / 60:F0} minutes";
        if (seconds < 86400) return $"{seconds / 3600:F0} hours";
        if (seconds < 31536000) return $"{seconds / 86400:F0} days";
        if (seconds < 3153600000) return $"{seconds / 31536000:F0} years";
        return "Centuries+";
    }

    /// <summary>
    /// Counts runs of 3+ characters that are sequential (ascending/descending)
    /// in ASCII value or on a QWERTY keyboard row.
    /// </summary>
    private static int CountSequentialRuns(string password)
    {
        const string qwertyRows = "qwertyuiop|asdfghjkl|zxcvbnm|1234567890";
        string lower = password.ToLowerInvariant();
        int runs = 0;

        for (int i = 0; i < lower.Length - 2; i++)
        {
            char a = lower[i], b = lower[i + 1], c = lower[i + 2];

            // ASCII sequential (abc, 123, etc.)
            if (b - a == 1 && c - b == 1) { runs++; continue; }
            // ASCII reverse sequential (cba, 321)
            if (a - b == 1 && b - c == 1) { runs++; continue; }

            // QWERTY row adjacency
            string triple = new(new[] { a, b, c });
            foreach (var row in qwertyRows.Split('|'))
            {
                if (row.Contains(triple)) { runs++; break; }
            }
        }

        return runs;
    }
}
