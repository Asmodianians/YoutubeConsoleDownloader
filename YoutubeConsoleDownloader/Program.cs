using Microsoft.Extensions.Configuration;
using Spectre.Console;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using YoutubeExplode;
using YoutubeExplode.Common;
using YoutubeExplode.Converter;
using YoutubeExplode.Videos.Streams;



namespace YoutubeConsoleDownloader
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            // --- 1. Загрузка конфигурации ---
            var config = new ConfigurationBuilder().SetBasePath(Directory.GetCurrentDirectory()).AddJsonFile("appsettings.json").Build().Get<AppSettings>() ?? new AppSettings();

            AnsiConsole.MarkupLine($"[#32CD32]Программа YoutubeConsoleDownloader запущена![/]");
            AnsiConsole.MarkupLine($"[bold cyan]Настройки для скачиваемого видео: " + config.PreferredQuality + "p[/]");
            AnsiConsole.MarkupLine($"[bold cyan]Настройки можно изменить в appsettings.json\nCсылки на автоматическое скачивание необходимо поместить в файл DownloadList.txt[/]");


            var youtube = new YoutubeClient();
            string playlistFilePath = "DownloadList.txt";

            // --- 2. Блок меню ввода URL или считывания плейлиста ---

            //Меню тебю
            while (true)
            {
                var menuOptions = new[]
                {
                    "\n--- Главное меню ---",
                    "1. Вставить ссылку на видео/плейлист",
                    "2. Загрузить из файла DownloadList.txt",
                    "0. Выход"
                };

                foreach (var option in menuOptions)
                {
                    AnsiConsole.MarkupLine($"[#32CD32]{option}[/]");
                }

                AnsiConsole.Markup($"[#32CD32]Выберите пункт: [/]");

                string choice = Console.ReadLine();

                if (choice == "1") //передаем ссылку в ProcessUrl
                {
                    AnsiConsole.Markup($"[#32CD32]Введите URL: [/]");
                    string url = Console.ReadLine();
                    await ProcessUrl(youtube, url, config);
                }
                else if (choice == "2")
                {
                    if (File.Exists(playlistFilePath))//чтим из файла построчно
                    {
                        // Пропускаем пустые и строки с "#" и убираем комменты "//" в конце строки
                        string[] urls = File.ReadAllLines(playlistFilePath)
                        .Where(line => !string.IsNullOrWhiteSpace(line) && !line.Trim().StartsWith("#")).ToArray();

                        foreach (string url in urls)
                        {
                            if (!string.IsNullOrWhiteSpace(url))
                            {
                                await ProcessUrl(youtube, url, config);
                            }
                        }
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[red]Файл DownloadList.txt не найден.[/]");
                    }
                }
                else if (choice == "0")//ехит
                {
                    break;
                }
            }

            // --- 3. Блок анализа ссылок ---

            //определяем тип ссылки (Освенцим или ГУЛАГ (О_0);)
            static async Task ProcessUrl(YoutubeClient youtube, string url, AppSettings config)
            {
                try
                {
                    if (url.Contains("list="))
                    {
                        AnsiConsole.MarkupLine($"[#7FFF00]Обнаружен плейлист. Получение видео...[/]");
                        var playlist = await youtube.Playlists.GetAsync(url);
                        var videos = await youtube.Playlists.GetVideosAsync(playlist.Id);

                        Console.WriteLine($"Плейлист: {playlist.Title} ({videos.Count} видео)");

                        foreach (var video in videos)
                        {
                            await DownloadVideo(youtube, video.Url, config);
                        }
                    }
                    else
                    {
                        await DownloadVideo(youtube, url, config);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка обработки {url}: {ex.Message}");
                }
            }

            // --- 4. Блок загрузки и сохранения видео ---

            static async Task DownloadVideo(YoutubeClient youtube, string url, AppSettings config)
            {

                try
                {
                    // 1. Получение информации о видео
                    var video = await youtube.Videos.GetAsync(url);
                    var outputFileName = SanitizeFilename(video.Title);

                    AnsiConsole.MarkupLineInterpolated($"[green]Скачивание:[/] [bold cyan]{outputFileName}[/]");
                    AnsiConsole.MarkupLineInterpolated($"[green]Продолжительность:[/] [bold cyan]{video.Duration}[/]");
                    //AnsiConsole.MarkupLine($"[green]Описание:[/] [bold cyan]{video.Description}[/]");

                    // 2. Получение манифеста стримов
                    var streamManifest = await youtube.Videos.Streams.GetManifestAsync(video.Id);

                    // ... дальнейшая логика скачивания ...

                    // --- 3. Поиск видео и аудио потоков ---
                    var videoStreamInfo = streamManifest
                        .GetVideoStreams()
                        .Where(s => s.Container == Container.Mp4 && s.VideoQuality.MaxHeight == config.PreferredQuality)
                        .OrderByDescending(s => s.VideoQuality.Framerate)
                        .FirstOrDefault();

                    // Если желаемое качество не найдено, используется резервный вариант.
                    if (videoStreamInfo == null)
                    {
                        AnsiConsole.MarkupLine($"[yellow]Видео в формате {config.PreferredQuality}p не найдено. Ищу наилучшую доступную альтернативу...[/]");

                        videoStreamInfo = streamManifest
                            .GetVideoStreams()
                            .Where(s => s.Container == Container.Mp4 && s.VideoQuality.MaxHeight < config.PreferredQuality)
                            .OrderByDescending(s => s.VideoQuality.MaxHeight + s.VideoQuality.Framerate)
                            .FirstOrDefault();
                        if (videoStreamInfo == null)
                        {
                            AnsiConsole.MarkupLine("[red]Ошибка: Не найден подходящий видеопоток в формате MP4.[/]");
                            return;
                        }
                        AnsiConsole.MarkupLineInterpolated($"[aqua]Найден лучший вариант: {videoStreamInfo.VideoQuality.Label}[/]");
                    }

                    // Поиск лучшего аудио потока
                    var audioStreamInfo = streamManifest
                        .GetAudioStreams()
                        .Where(s => s.Container == Container.Mp4)
                        .GetWithHighestBitrate();

                    if (audioStreamInfo == null)
                    {
                        AnsiConsole.MarkupLine("[red]Ошибка: Аудиопоток не найден.[/]");
                        return;
                    }

                    // --- 4. Скачивайте стрима и объединение их в видеофайл. ---

                    // Определение пути сохранения. По умолчанию используется корневая папка.
                    // 1. Получаем базовый путь, где запущена программа
                    string appDirectory = AppDomain.CurrentDomain.BaseDirectory;
                    // 2. Определяем путь к папке Downloads внутри папки программы
                    string downloadsPath = Path.Combine(appDirectory, "Downloads");
                    // 3. Создаем папку, если её нет
                    if (!Directory.Exists(downloadsPath))
                    {
                        Directory.CreateDirectory(downloadsPath);
                    }

                    //скачивем в папку
                    var outputPath = config.OutputPath ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonVideos);
                    var outputFile = Path.Combine(outputPath, $"{outputFileName}.mp4");

                    //выводим прогрес бар
                    await AnsiConsole.Progress()
                        .StartAsync(async ctx =>
                        {
                            var progressTask = ctx.AddTask($"[green]Загрузка видео: [/]");

                            var progress = new Progress<double>(percent => progressTask.Increment(percent * 100 - progressTask.Percentage));

                            await youtube.Videos.DownloadAsync(
                                [audioStreamInfo, videoStreamInfo],
                                new ConversionRequestBuilder(outputFile).Build(),
                                progress
                            );
                        });

                    AnsiConsole.MarkupLine($"[bold green]Видео успешно сохранено![/]");

                    //Пауза 10 сек
                    AnsiConsole.MarkupLine($"[bold green]Пауза 10 секунд.[/]");
                    System.Threading.Thread.Sleep(10000);
                }
                catch (YoutubeExplode.Exceptions.VideoUnavailableException)
                {
                    AnsiConsole.MarkupLine("[red]Ошибка: Видео недоступно (удалено или скрыто).[/]");
                }
                catch (ArgumentException)
                {
                    AnsiConsole.MarkupLine("[red]Ошибка: Некорректный URL видео.[/]");
                }
                catch (HttpRequestException)
                {
                    AnsiConsole.MarkupLine("[red]Ошибка: Проблемы с интернет-соединением.[/]");
                }
                catch (Exception ex)
                {
                    // Обработка любых других непредвиденных ошибок
                    AnsiConsole.MarkupLine($"[red]Произошла непредвиденная ошибка:[/] {ex.Message}");
                }

                //замена имени файла если отсутствует или некорректно
                static string SanitizeFilename(string filename)
                {
                    Random _random = new Random();

                    if (string.IsNullOrWhiteSpace(filename))
                    {
                        // Используем его
                        int videoId = _random.Next();
                        return $"video{videoId}";
                    }

                    foreach (var c in Path.GetInvalidFileNameChars())
                    {
                        filename = filename.Replace(c.ToString(), "");
                    }

                    if (string.IsNullOrWhiteSpace(filename))
                    {
                        // Используем его
                        int videoId = _random.Next();
                        return $"video{videoId}";
                    }

                    //устанавливаем максимально разрешенную длинну имени файла
                    const int MaxFileNameLength = 200;

                    if (filename.Length > MaxFileNameLength)
                    {
                        filename = filename.Substring(0, MaxFileNameLength);
                    }

                    return filename;
                }
            }
        }
        // --- 5. Настройки ---
        public class AppSettings()
        { //Класс для доступа к содержимому appsettings.json
            public int PreferredQuality { get; set; } = 720;
            public string OutputPath { get; set; }
        }

    }
}
