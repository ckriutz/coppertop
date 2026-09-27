using System.Security.Cryptography;
using System.Text;

namespace Coppertop.Trader.Kraken;

public static class KrakenSigner
{
    // API-Sign = Base64(HMAC-SHA512(Base64Decode(secret), path + SHA256(nonce + postData)))
    public static string Sign(string urlPath, string nonce, string postData, string base64Secret)
    {
        var sha = SHA256.HashData(Encoding.UTF8.GetBytes(nonce + postData));
        var pathBytes = Encoding.UTF8.GetBytes(urlPath);
        var message = new byte[pathBytes.Length + sha.Length];
        pathBytes.CopyTo(message, 0);
        sha.CopyTo(message, pathBytes.Length);

        var mac = HMACSHA512.HashData(Convert.FromBase64String(base64Secret), message);
        return Convert.ToBase64String(mac);
    }
}
