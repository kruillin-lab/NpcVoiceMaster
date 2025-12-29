using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json; // Used for parsing JSON from AllTalk
using System.Threading.Tasks;

namespace NPCVoiceMaster
{
    public sealed class Plugin : IDalamudPlugin
    {
        public string Name => "NPC Voice Master";
        private const string CommandName = "/npcvoice";

        private IDalamudPluginInterface PluginInterface { get; init; }
        private ICommandManager CommandManager { get; init; }
        public Configuration Configuration { get; init; }
        public WindowSystem WindowSystem { get; init; }
        private ConfigWindow ConfigWindow { get; init; }

        // HTTP Client for talking to AllTalk
        private readonly HttpClient _httpClient = new HttpClient();

        public Plugin(
            IDalamudPluginInterface pluginInterface,
            ICommandManager commandManager)
        {
            this.PluginInterface = pluginInterface;
            this.CommandManager = commandManager;

            // Load Config
            this.Configuration = this.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            this.Configuration.Initialize(this.PluginInterface);

            // UI Setup
            this.WindowSystem = new WindowSystem("NPCVoiceMaster");
            // Pass 'this' (the plugin) so the window can access FetchAllTalkVoicesAsync
            this.ConfigWindow = new ConfigWindow(this);
            this.WindowSystem.AddWindow(this.ConfigWindow);

            this.PluginInterface.UiBuilder.Draw += DrawUI;
            this.PluginInterface.UiBuilder.OpenConfigUi += DrawConfigUI;

            // Command
            this.CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Opens the NPC Voice Master config."
            });
        }

        public void Dispose()
        {
            this.WindowSystem.RemoveAllWindows();
            this.ConfigWindow.Dispose();
            this.CommandManager.RemoveHandler(CommandName);
            this.PluginInterface.UiBuilder.Draw -= DrawUI;
            this.PluginInterface.UiBuilder.OpenConfigUi -= DrawConfigUI;
            _httpClient.Dispose();
        }

        private void OnCommand(string command, string args)
        {
            this.ConfigWindow.IsOpen = !this.ConfigWindow.IsOpen;
        }

        private void DrawUI()
        {
            this.WindowSystem.Draw();
        }

        private void DrawConfigUI()
        {
            this.ConfigWindow.IsOpen = true;
        }

        // The missing method your ConfigWindow is calling
        public async Task<List<string>> FetchAllTalkVoicesAsync()
        {
            var url = this.Configuration.AllTalkBaseUrl;
            if (string.IsNullOrEmpty(url)) return new List<string>();

            try
            {
                // Example call to AllTalk API to get voices
                // Adjust endpoint "/api/voices" if AllTalk uses a different one
                var response = await _httpClient.GetStringAsync($"{url}/api/voices");

                // Assuming response is a JSON array of strings
                // If AllTalk returns complex objects, we would need to parse differently
                var voices = JsonSerializer.Deserialize<List<string>>(response);
                return voices ?? new List<string>();
            }
            catch (Exception)
            {
                // Silently fail or log error
                return new List<string> { "Error connecting to AllTalk" };
            }
        }
    }
}