using System;
using System.Security.Cryptography;
using System.Text;

namespace SimHub.Plugin.TapoSwitch.Tapo
{
    /// <summary>
    /// Implements the TP-Link KLAP encryption session used by Tapo (and newer Kasa)
    /// smart plugs since the 2023 firmware update.
    ///
    /// Ported from the reference Python implementation in python-kasa
    /// (kasa/transports/klaptransport.py), which itself credits Simon Wilkinson
    /// and Chris Wheeldon for the original protocol write-up:
    /// https://gist.github.com/chriswheeldon/3b17d974db3817613c69191c0480fe5
    ///
    /// This class implements the "V2" hash variant, which is what Tapo branded
    /// devices (and Kasa devices updated after 2023) use.
    /// </summary>
    internal sealed class KlapCipher
    {
        private readonly byte[] _key;      // AES-128 key
        private readonly byte[] _ivBase;   // first 12 bytes of the derived IV
        private readonly byte[] _sig;      // 28 byte signing seed
        private int _seq;                  // signed 32 bit sequence number

        public int Sequence => _seq;

        private KlapCipher(byte[] key, byte[] ivBase, byte[] sig, int seq)
        {
            _key = key;
            _ivBase = ivBase;
            _sig = sig;
            _seq = seq;
        }

        public static KlapCipher Create(byte[] localSeed, byte[] remoteSeed, byte[] authHash)
        {
            byte[] key = Sha256(Concat(Encoding.ASCII.GetBytes("lsk"), localSeed, remoteSeed, authHash));
            Array.Resize(ref key, 16);

            byte[] fullIv = Sha256(Concat(Encoding.ASCII.GetBytes("iv"), localSeed, remoteSeed, authHash));
            byte[] ivBase = new byte[12];
            Array.Copy(fullIv, 0, ivBase, 0, 12);
            int seq = ReadInt32BigEndian(fullIv, 28);

            byte[] sig = Sha256(Concat(Encoding.ASCII.GetBytes("ldk"), localSeed, remoteSeed, authHash));
            Array.Resize(ref sig, 28);

            return new KlapCipher(key, ivBase, sig, seq);
        }

        /// <summary>Encrypts a UTF8 JSON payload. Increments the sequence number first.</summary>
        public (byte[] Payload, int Seq) Encrypt(string message)
        {
            _seq++;
            byte[] iv = BuildIv(_seq);
            byte[] plain = Encoding.UTF8.GetBytes(message);

            byte[] cipherText;
            using (var aes = Aes.Create())
            {
                aes.Key = _key;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                using (var encryptor = aes.CreateEncryptor())
                {
                    cipherText = encryptor.TransformFinalBlock(plain, 0, plain.Length);
                }
            }

            byte[] seqBytes = PackInt32BigEndian(_seq);
            byte[] signature = Sha256(Concat(_sig, seqBytes, cipherText));

            return (Concat(signature, cipherText), _seq);
        }

        /// <summary>Decrypts a response for the sequence number that was just used to encrypt.</summary>
        public string Decrypt(byte[] response)
        {
            if (response.Length < 32)
            {
                throw new InvalidOperationException("KLAP response too short to contain a signature.");
            }

            byte[] cipherText = new byte[response.Length - 32];
            Array.Copy(response, 32, cipherText, 0, cipherText.Length);

            byte[] iv = BuildIv(_seq);

            byte[] plain;
            using (var aes = Aes.Create())
            {
                aes.Key = _key;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                using (var decryptor = aes.CreateDecryptor())
                {
                    plain = decryptor.TransformFinalBlock(cipherText, 0, cipherText.Length);
                }
            }

            return Encoding.UTF8.GetString(plain);
        }

        private byte[] BuildIv(int seq)
        {
            byte[] iv = new byte[16];
            Array.Copy(_ivBase, 0, iv, 0, 12);
            byte[] seqBytes = PackInt32BigEndian(seq);
            Array.Copy(seqBytes, 0, iv, 12, 4);
            return iv;
        }

        internal static byte[] Sha256(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                return sha.ComputeHash(data);
            }
        }

        internal static byte[] Sha1(byte[] data)
        {
            using (var sha = SHA1.Create())
            {
                return sha.ComputeHash(data);
            }
        }

        internal static byte[] Md5(byte[] data)
        {
            using (var md5 = MD5.Create())
            {
                return md5.ComputeHash(data);
            }
        }

        internal static byte[] Concat(params byte[][] parts)
        {
            int total = 0;
            foreach (var p in parts) total += p.Length;
            byte[] result = new byte[total];
            int offset = 0;
            foreach (var p in parts)
            {
                Buffer.BlockCopy(p, 0, result, offset, p.Length);
                offset += p.Length;
            }
            return result;
        }

        internal static byte[] PackInt32BigEndian(int value)
        {
            byte[] b = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian) Array.Reverse(b);
            return b;
        }

        internal static int ReadInt32BigEndian(byte[] data, int offset)
        {
            byte[] b = new byte[4];
            Array.Copy(data, offset, b, 0, 4);
            if (BitConverter.IsLittleEndian) Array.Reverse(b);
            return BitConverter.ToInt32(b, 0);
        }
    }
}
