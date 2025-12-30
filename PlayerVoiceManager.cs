
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

public class PlayerVoiceManager
{
    private readonly string filePath;
    private Dictionary<string, string> playerVoices;

    public PlayerVoiceManager(string filePath)
    {
        this.filePath = filePath;
        this.playerVoices = LoadPlayerVoices();
    }

    // Load player voices from the JSON file
    public Dictionary<string, string> LoadPlayerVoices()
    {
        if (File.Exists(filePath))
        {
            string json = File.ReadAllText(filePath);
            return JsonConvert.DeserializeObject<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        }
        return new Dictionary<string, string>();
    }

    // Save player voices to the JSON file
    public void SavePlayerVoices()
    {
        string json = JsonConvert.SerializeObject(playerVoices, Formatting.Indented);
        File.WriteAllText(filePath, json);
    }

    // Get the voice assigned to a player
    public string GetPlayerVoice(string playerName)
    {
        if (string.IsNullOrEmpty(playerName)) return null;  // Return null if playerName is empty
        return playerVoices.ContainsKey(playerName) ? playerVoices[playerName] : null;
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
