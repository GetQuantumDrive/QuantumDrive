namespace quantum_drive.Models;

public class VaultLimitReachedException(int limit)
    : InvalidOperationException(
        $"Free tier allows {limit} vault. Upgrade to Pro for unlimited vaults.");
