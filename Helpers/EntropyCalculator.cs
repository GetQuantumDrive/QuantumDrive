using System;
using System.Linq;

namespace quantum_drive.Helpers;

public static class EntropyCalculator
{
    public static double CalculateBits(string password)
    {
        if (string.IsNullOrEmpty(password)) return 0;

        int poolSize = 0;
        if (password.Any(char.IsLower)) poolSize += 26;
        if (password.Any(char.IsUpper)) poolSize += 26;
        if (password.Any(char.IsDigit)) poolSize += 10;
        if (password.Any(c => !char.IsLetterOrDigit(c))) poolSize += 32;

        if (poolSize == 0) return 0;

        return password.Length * Math.Log2(poolSize);
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
        double seconds = Math.Pow(2, bits) / 1_000_000_000;

        if (seconds < 1) return "Instantly";
        if (seconds < 60) return $"{seconds:F0} seconds";
        if (seconds < 3600) return $"{seconds / 60:F0} minutes";
        if (seconds < 86400) return $"{seconds / 3600:F0} hours";
        if (seconds < 31536000) return $"{seconds / 86400:F0} days";
        if (seconds < 3153600000) return $"{seconds / 31536000:F0} years";
        return "Centuries+";
    }
}
