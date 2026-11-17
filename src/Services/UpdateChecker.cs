using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace WirelessBatteryHub.Services;

public static class UpdateChecker
{
    public static string CurrentVersion
    {
        get
        {
            string? info = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(info)) return "1.0.0";
            return info.Split('|')[0];
        }
    }

    public static string? GitHubRepo
    {
        get
        {
            string? info = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(info)) return null;
            string[] parts = info.Split('|');
            return parts.Length > 1 && parts[1].Contains('/') ? parts[1].Trim() : null;
        }
    }

    public static async Task CheckAsync(IWin32Window owner)
    {
        string? repo = GitHubRepo;
        if (string.IsNullOrWhiteSpace(repo))
        {
            MessageBox.Show(owner,
                $"Текущая версия: {CurrentVersion}\nРепозиторий определяется автоматически при сборке на GitHub.",
                "О программе", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("WirelessBatteryHub/1.0");

            HttpResponseMessage resp = await http.GetAsync($"https://api.github.com/repos/{repo}/releases/latest");
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                MessageBox.Show(owner,
                    $"У вас версия v{CurrentVersion}.\nВ репозитории {repo} пока нет опубликованных релизов (тегов v*).",
                    "Обновления", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            resp.EnsureSuccessStatusCode();
            using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

            string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            string url = doc.RootElement.GetProperty("html_url").GetString() ?? $"https://github.com/{repo}/releases";
            string cleanLatest = tag.TrimStart('v', 'V');

            if (Version.TryParse(cleanLatest, out Version? vLatest) &&
                Version.TryParse(CurrentVersion, out Version? vCur) &&
                vLatest > vCur)
            {
                DialogResult ans = MessageBox.Show(owner,
                    $"Доступна новая версия v{cleanLatest} (у вас v{CurrentVersion}).\nОткрыть страницу скачивания?",
                    "Доступно обновление", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

                if (ans == DialogResult.Yes)
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else
            {
                MessageBox.Show(owner,
                    $"У вас актуальная версия (v{CurrentVersion}).",
                    "Обновления", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner,
                $"Не удалось проверить обновления: {ex.Message}",
                "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
