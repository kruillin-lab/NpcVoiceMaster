
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

public class PlayerVoiceManager
{
    private readonly string filePath;
    // Store player voice assignments. Use case-insensitive keys so that player names
    // are treated the same regardless of capitalization.
    private Dictionary<string, string> playerVoices;

        public PlayerVoiceManager(string filePath)
        {
            this.filePath = filePath;
            // Initialize the dictionary with loaded values and ensure case-insensitive lookup.
            // When loading from disk, normalize all player names to "First Last" (ignoring server
            // names) so that assignments are independent of a player's world/server. This makes it
            // unnecessary to include the server name when assigning a voice.
            var loaded = LoadPlayerVoices();
            this.playerVoices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in loaded)
            {
                var norm = NormalizeName(kv.Key);
                this.playerVoices[norm] = kv.Value;
            }
        }

        /// <summary>
        /// Normalize a character name by stripping any server/world component. Input names are
        /// expected to be either "First Last" or "First Last Server"; this method returns
        /// "First Last" in both cases. Names are trimmed and multiple spaces are collapsed.
        /// </summary>
        private static string NormalizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            var parts = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // If there are at least two parts, return the first and second (first and last name).
            if (parts.Length >= 2)
                return parts[0] + " " + parts[1];
            return parts[0];
        }

    /// <summary>
    /// Returns a copy of all current player voice assignments. A new dictionary is returned to
    /// prevent callers from modifying the internal state of this manager directly. Keys are
    /// compared using a case-insensitive comparer.
    /// </summary>
    public Dictionary<string, string> GetAllPlayerVoices()
    {
        return new Dictionary<string, string>(playerVoices, StringComparer.OrdinalIgnoreCase);
    }

    // Load player voices from the JSON file
    public Dictionary<string, string> LoadPlayerVoices()
    {
        if (File.Exists(filePath))
        {
            var json = File.ReadAllText(filePath);
            // Deserialize into a temporary dictionary (case-sensitive) and then copy
            // into a new dictionary with a case-insensitive comparer. This avoids
            // having multiple conflicting entries for names that differ only by case.
            var tmp = JsonConvert.DeserializeObject<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
            return new Dictionary<string, string>(tmp, StringComparer.OrdinalIgnoreCase);
        }
        // Create an empty case-insensitive dictionary by default.
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    // Save player voices to the JSON file
    public void SavePlayerVoices()
    {
        string json = JsonConvert.SerializeObject(playerVoices, Formatting.Indented);
        File.WriteAllText(filePath, json);
    }

    // Get the voice assigned to a player
    public string? GetPlayerVoice(string playerName)
    {
        // Return null if playerName is null or empty
        if (string.IsNullOrWhiteSpace(playerName)) return null;
        var norm = NormalizeName(playerName);
        // Try to retrieve the voice assignment using the normalized name; return null if not found
        return playerVoices.TryGetValue(norm, out var voice) ? voice : null;
    }

    // Assign a voice to a player
    public void AssignVoiceToPlayer(string playerName, string voiceFile)
    {
        if (!string.IsNullOrEmpty(playerName) && !string.IsNullOrEmpty(voiceFile))
        {
            var norm = NormalizeName(playerName);
            if (!playerVoices.ContainsKey(norm))
            {
                playerVoices.Add(norm, voiceFile);
            }
            else
            {
                playerVoices[norm] = voiceFile;
            }
            SavePlayerVoices(); // Save the updated voices
        }
    }

    // Remove the voice assignment for a player
    public void RemoveVoiceFromPlayer(string playerName)
    {
        var norm = NormalizeName(playerName);
        if (playerVoices.ContainsKey(norm))
        {
            playerVoices.Remove(norm);
            SavePlayerVoices(); // Save the updated voices
        }
    }
}
