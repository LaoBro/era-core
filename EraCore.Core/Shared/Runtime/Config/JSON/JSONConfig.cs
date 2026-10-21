using MinorShift.Emuera.Files;
//新設したコンフィグ設定のロード、セーブ、公開を担当する。
using System.Text.Json;
using MinorShift.Emuera.GameView;


namespace MinorShift.Emuera.Runtime.Config.JSON;
static class JSONConfig
{
	// Some runtime types read this during static initialization before the startup loader runs.
	public static JSONConfigData Data = new();

	/// <summary>setting.json 在应用私有域内的相对名——唯一 IO 口子是 <see cref="AppDataIo"/>。</summary>
	const string _configFileName = "setting.json";

	static AppDataPath ConfigFile => AppDataIo.Resolve(_configFileName);

	/// <summary>
	/// setting.json 只落在应用私有域（F-4 豁免域），故经 <see cref="AppDataIo"/> 读写而非裸
	/// <c>File.*</c> / <c>Directory.*</c>——曾直连的局面已收敛。
	/// </summary>
	public static void Load()
	{
		try { AppDataPaths.EnsureDirectory(); }
		catch { /* ignore directory creation failure in headless mode */ }

		if (!AppDataIo.FileExists(ConfigFile))
		{
			var defaultData = new JSONConfigData();
			var defaultJson = JsonSerializer.Serialize(defaultData, EmueraJsonContext.Default.JSONConfigData);
			try { AppDataIo.WriteAllText(ConfigFile, defaultJson); }
			catch { /* ignore write failure in headless mode */ }
		}

		if (AppDataIo.FileExists(ConfigFile))
		{
			var json = AppDataIo.ReadAllText(ConfigFile);
			Data = JsonSerializer.Deserialize(json, EmueraJsonContext.Default.JSONConfigData) ?? new JSONConfigData();
		}
		else
		{
			Data = new JSONConfigData();
		}
	}

	public static void Save()
	{
		var json = JsonSerializer.Serialize(Data, EmueraJsonContext.Default.JSONConfigData);
		AppDataIo.WriteAllText(ConfigFile, json);
	}
}
