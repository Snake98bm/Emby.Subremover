using System;
using System.Security.Cryptography;

namespace Emby.Subremover.Core
{
    /// <summary>
    /// The secret in the "open in a new window" and "download" links of the settings page. The log address itself
    /// needs no Emby login (a new browser window doesn't carry one), so only someone who saw the settings page, which
    /// is for admins, can open a log. A new secret is made every day.
    /// </summary>
    public sealed class LogLinkKey
    {
        public static readonly TimeSpan ValidFor = TimeSpan.FromHours(24);

        private readonly object _lock = new object();
        private string _key;
        private DateTime _madeUtc;

        /// <summary>The secret for links shown now, made anew once the last one is a day old.</summary>
        public string Current(DateTime nowUtc)
        {
            lock (_lock)
            {
                if (_key == null || nowUtc - _madeUtc > ValidFor)
                {
                    var bytes = new byte[16];
                    using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
                    _key = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
                    _madeUtc = nowUtc;
                }
                return _key;
            }
        }

        public bool IsValid(string key, DateTime nowUtc)
        {
            lock (_lock)
            {
                if (_key == null || string.IsNullOrEmpty(key) || nowUtc - _madeUtc > ValidFor || key.Length != _key.Length) return false;
                // Compares every character, so the time taken doesn't hint at how much of a guess was right.
                var diff = 0;
                for (var i = 0; i < key.Length; i++) diff |= key[i] ^ _key[i];
                return diff == 0;
            }
        }
    }
}
