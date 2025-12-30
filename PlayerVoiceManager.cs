
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace NPCVoiceMaster
{
    /// <summary>
    /// Manages per-player voice assignments and provides persistence via JSON.
    /// </summary>
    public class PlayerVoiceManager
{
        private readonly string _filePath;

        // Internal mapping of player names to their assigned voice names.
        private readonly Dictionary<string, string> _playerVoices = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Initializes a new instance of <see cref="PlayerVoiceManager"/> and loads any existing assignments from disk.
        /// </summary>
        /// <param name="filePath">The path to the JSON file used for persistence.</param>
        public PlayerVoiceManager(string filePath)
        {
            _filePath = filePath;
            LoadPlayerVoices();
        }

        /// <summary>
        /// Gets a read-only view of all current player voice assignments.
        /// </summary>
        public IReadOnlyDictionary<string, string> PlayerVoices => _playerVoices;

        /// <summary>
        /// Loads player voices from the JSON file into the internal dictionary. If the file is missing or
        /// malformed, the dictionary is left empty.
        /// </summary>
        private void LoadPlayerVoices()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_filePath) && File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    var data = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                    _playerVoices.Clear();
                    if (data != null)
                    {
                        foreach (var kv in data)
                        {
                            var name = (kv.Key ?? "").Trim();
                            var voice = (kv.Value ?? "").Trim();
                            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(voice))
                                _playerVoices[name] = voice;
                        }
                    }
                }
            }
            catch
            {
                // If loading fails, leave dictionary empty; plugin will recreate file on next save.
            }
        }

        /// <summary>
        /// Persists the current player voice assignments to disk.
        /// </summary>
        public void SavePlayerVoices()
        {
            try
            {
                var json = JsonConvert.SerializeObject(_playerVoices, Formatting.Indented);
                File.WriteAllText(_filePath, json);
            }
            catch
            {
                // Swallow exceptions; callers may log if desired.
            }
        }

        /// <summary>
        /// Returns the voice assigned to a player, or <c>null</c> if no assignment exists.
        /// </summary>
        /// <param name="playerName">The name of the player.</param>
        /// <returns>The assigned voice name, or <c>null</c> if none is assigned.</returns>
        public string? GetPlayerVoice(string playerName)
        {
            if (string.IsNullOrWhiteSpace(playerName))
                return null;

            return _playerVoices.TryGetValue(playerName.Trim(), out var voice) ? voice : null;
        }

        /// <summary>
        /// Assigns a voice to a player. If the player already has an assignment, it is overwritten.
        /// The new assignment is immediately persisted to disk.
        /// </summary>
        /// <param name="playerName">The player's name.</param>
        /// <param name="voiceName">The voice to assign.</param>
        public void AssignVoiceToPlayer(string playerName, string voiceName)
        {
            if (string.IsNullOrWhiteSpace(playerName) || string.IsNullOrWhiteSpace(voiceName))
                return;

            _playerVoices[playerName.Trim()] = voiceName.Trim();
            SavePlayerVoices();
        }

        /// <summary>
        /// Removes a player's voice assignment, if one exists. Changes are persisted to disk.
        /// </summary>
        /// <param name="playerName">The player whose assignment should be removed.</param>
        public void RemoveVoiceFromPlayer(string playerName)
        {
            if (string.IsNullOrWhiteSpace(playerName))
                return;

            if (_playerVoices.Remove(playerName.Trim()))
            {
                SavePlayerVoices();
            }
        }
    }
}
