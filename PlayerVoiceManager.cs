
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
        // Initialize the dictionary with loaded values and ensure case-insensitive lookup
        this.playerVoices = LoadPlayerVoices();
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

        // Try to retrieve the voice assignment; return null if not found
        return playerVoices.TryGetValue(playerName, out var voice) ? voice : null;
    }

    // Assign a voice to a player
    public void AssignVoiceToPlayer(string playerName, string voiceFile)
    {
        if (!string.IsNullOrEmpty(playerName) && !string.IsNullOrEmpty(voiceFile))
        {
            if (!playerVoices.ContainsKey(playerName))
            {
                playerVoices.Add(playerName, voiceFile);
            }
            else
            {
                playerVoices[playerName] = voiceFile;
            }
            SavePlayerVoices(); // Save the updated voices
        }
    }

    // Remove the voice assignment for a player
    public void RemoveVoiceFromPlayer(string playerName)
    {
        if (playerVoices.ContainsKey(playerName))
        {
            playerVoices.Remove(playerName);
            SavePlayerVoices(); // Save the updated voices
        }
    }
}
