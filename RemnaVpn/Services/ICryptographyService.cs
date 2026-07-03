namespace RemnaVpn.Services
{
    public interface ICryptographyService
    {
        string Encrypt(string plainText);
        string Decrypt(string encryptedText);
        string GenerateHwid();
    }
}
