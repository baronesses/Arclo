using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Orbit.App.Tools;
using Orbit.Core;

namespace Orbit.App;

public sealed partial class MainWindow
{
    private sealed record ToolCommand(string Label,string Keywords,Action Run,bool SearchOnly=false);
    private TextBox? _toolSearchBox;
    private static string ToolKeywords(string label)
    {
        var words=new List<string>();
        if(label.Contains("Скачать",StringComparison.OrdinalIgnoreCase))words.Add("download youtube ytdlp yt-dlp ссылка link url музыка music трек mp3 mp4 видео");
        void Aliases(string part,string aliases){if(label.Contains(part,StringComparison.OrdinalIgnoreCase))words.Add(aliases);}
        Aliases("пресет","preset настройки рецепт шаблон");Aliases("Повтор","repeat рецепт последний");Aliases("Избран","favorites любимые");
        Aliases("история","history очередь задания cancel отмена");Aliases("результат","results recent готовые");
        Aliases("Цепоч","chain пакет шаги workflow");Aliases("SHA","sha256 sha-256 hash хеш хэш checksum контрольная сумма сверить");
        Aliases("контрольных","sha256 sha-256 hash хеш хэш checksum manifest сверить");Aliases("Сведения","info свойства details размер формат");
        Aliases("ZIP","archive zip архив упаковать распаковать");Aliases("TAR","archive tar gzip tgz архив упаковать распаковать");
        Aliases("7z","archive sevenzip 7zip архив упаковать распаковать");
        Aliases("Переимен","rename имена нумерация");Aliases("Распознать","ocr text текст распознавание");
        Aliases("Повернуть","rotate поворот ориентация");Aliases("отразить","flip зеркало отражение");Aliases("JPEG","jpg jpeg фон transparency background");
        Aliases("Контактный","contact sheet коллаж миниатюры");Aliases("метаданные","metadata exif приватность очистка");
        Aliases("Водяной","watermark logo text copyright надпись логотип текст защита");
        Aliases("Обрезать по времени","trim cut фрагмент начало конец");Aliases("дорожк","audio track звук потоки извлечение");
        Aliases("Потоки","streams кодек codec аудио видео");Aliases("GIF","анимация gif");Aliases("Размер и звук","resize scale mute fps video частота кадров");
        Aliases("Кадры","frames png interval интервал видео");Aliases("Громкость","volume fade плавно затухание звук");
        Aliases("Нормализация","lufs normalize loudness громкость звук");Aliases("Экспорт M4A","aac m4a opus ogg vorbis vbr конвертация аудио");
        Aliases("Теги","tags title artist album название исполнитель альбом метаданные");Aliases("обложку","cover art picture обложка картинка аудио");
        Aliases("Объединить PDF","merge pdf склеить страницы");Aliases("Выбрать страницы","pages pdf extract диапазон");
        Aliases("Разделить PDF","split pdf страницы");Aliases("оптимизация PDF","text images optimize pdf сжать извлечь текст изображения");
        Aliases("Выбрать файлы","open file открыть загрузить");Aliases("Выбрать папки","open folder каталог директория");
        Aliases("Настройки","settings бинд hotkey клавиша прозрачность автозапуск папка");Aliases("пользоваться","help guide инструкция помощь обучение первый запуск");
        Aliases("Диагностика","diagnostics engines версии компоненты ffmpeg ffprobe imagemagick poppler qpdf tesseract 7zip ocr проверить движок");
        return string.Join(' ',words);
    }
    private void AddSearchOnlyCommands(List<ToolCommand> tools)
    {
        if(_selectionLoading||_files.Count==0)return;
        void Add(ImageAction action,string label,string words) {
            int existing=tools.FindIndex(tool=>string.Equals(tool.Label,label,StringComparison.Ordinal));
            if(existing>=0)tools[existing]=tools[existing] with{Keywords=tools[existing].Keywords+" "+words};
            else tools.Add(new ToolCommand(label,words,()=>DispatchAction(action),true));
        }
        switch(_selectionKind){
            case ToolkitKind.Image:
                Add(ImageAction.Png,global::Orbit.Core.Localization.L.Get("Text_541120634FC7"),"png конвертация прозрачность image");Add(ImageAction.Bmp,global::Orbit.Core.Localization.L.Get("Text_75D1177D5688"),"bmp bitmap конвертация");
                Add(ImageAction.Tiff,global::Orbit.Core.Localization.L.Get("Text_22DE11741411"),"tif tiff конвертация");Add(ImageAction.Gif,global::Orbit.Core.Localization.L.Get("Text_961E919EBADD"),"gif статическое изображение");
                Add(ImageAction.Ico,global::Orbit.Core.Localization.L.Get("Text_1DCF25CD302E"),"ico icon иконка значок");Add(ImageAction.Svg,global::Orbit.Core.Localization.L.Get("Text_CA723B841999"),"svg встроенное изображение embedded без векторизации");
                Add(ImageAction.WebP,global::Orbit.Core.Localization.L.Get("Text_C1AD8D73BB22"),"webp конвертация изображение картинка");Add(ImageAction.Avif,global::Orbit.Core.Localization.L.Get("Text_C9F8D4A06052"),"avif конвертация изображение картинка");
                Add(ImageAction.Resize,global::Orbit.Core.Localization.L.Get("Text_7221AB07C347"),"resize width height ширина высота масштаб проценты");
                Add(ImageAction.Crop,global::Orbit.Core.Localization.L.Get("Text_88D33657F4D0"),"crop кадрирование область пропорции");Add(ImageAction.Compress,global::Orbit.Core.Localization.L.Get("Text_0BBB5E187EE9"),"compress compression оптимизация размер качество");break;
            case ToolkitKind.Audio:
                Add(ImageAction.AudioMp3,global::Orbit.Core.Localization.L.Get("Text_B0176AF75651"),"mp3 конвертация звук audio lame");Add(ImageAction.AudioWav,global::Orbit.Core.Localization.L.Get("Text_663438E44C20"),"wav pcm конвертация звук audio");
                Add(ImageAction.AudioFlac,global::Orbit.Core.Localization.L.Get("Text_3165C5ABD2B9"),"flac lossless конвертация звук audio без потерь");Add(ImageAction.AudioCompress,global::Orbit.Core.Localization.L.Get("Text_0EFC9019EBB4"),"compress bitrate mp3 128 битрейт звук");break;
            case ToolkitKind.Video:
                Add(ImageAction.VideoMp4,global::Orbit.Core.Localization.L.Get("Text_E1ED963FD1A4"),"mp4 h264 h.264 aac конвертация video");Add(ImageAction.VideoWebm,global::Orbit.Core.Localization.L.Get("Text_D2E0FBA10668"),"webm vp9 opus конвертация video");
                if(_selectionMetadata.All(m=>m.HasAudio))Add(ImageAction.ExtractMp3,global::Orbit.Core.Localization.L.Get("Text_5F85796B4A97"),"mp3 audio звук первая дорожка извлечь");
                Add(ImageAction.VideoFrame,global::Orbit.Core.Localization.L.Get("Text_0708BDDC7BC5"),"frame png снимок скриншот постер");Add(ImageAction.VideoCompress,global::Orbit.Core.Localization.L.Get("Text_E459D195A019"),"compress compression crf mp4 размер качество");break;
            case ToolkitKind.Pdf:
                Add(ImageAction.PdfPng,global::Orbit.Core.Localization.L.Get("Text_538812C0C121"),"pdf png page изображения растр экспорт");Add(ImageAction.PdfJpg,global::Orbit.Core.Localization.L.Get("Text_463263FB87B6"),"pdf jpg jpeg page изображения растр экспорт");break;
            case ToolkitKind.Zip:
                Add(ImageAction.ExtractZip,global::Orbit.Core.Localization.L.Get("Text_5B2E1690F47A"),"zip extract unzip извлечь новая папка");Add(ImageAction.ArchiveList,global::Orbit.Core.Localization.L.Get("Text_1BF7AE35DCD4"),"zip list txt содержимое список файлы");break;
            case ToolkitKind.Tar:
                Add(ImageAction.TarExtractSelected,global::Orbit.Core.Localization.L.Get("Text_62568D3F15F0"),"tar gzip tgz selected выборочно отметки содержимое папка");
                Add(ImageAction.ExtractTar,global::Orbit.Core.Localization.L.Get("Text_CC8C0C63FCAB"),"tar gzip tgz extract извлечь новая папка");Add(ImageAction.TarList,global::Orbit.Core.Localization.L.Get("Text_0DC51A635BA3"),"tar gzip tgz list txt содержимое список файлы");break;
            case ToolkitKind.SevenZip:
                Add(ImageAction.SevenZipExtractSelected,global::Orbit.Core.Localization.L.Get("Text_85876B508C61"),"7z 7zip sevenzip selected выборочно отметки содержимое папка");
                Add(ImageAction.Extract7z,global::Orbit.Core.Localization.L.Get("Text_3FB609D0A52C"),"7z sevenzip 7zip extract извлечь новая папка");Add(ImageAction.SevenZipList,global::Orbit.Core.Localization.L.Get("Text_0FF94682FF19"),"7z sevenzip 7zip list txt содержимое список файлы");break;
        }
    }
    private void RenderToolSearch(IReadOnlyList<ToolCommand> tools)
    {
        int panelVersion=_comparisonVersion,loadVersion=_loadVersion;var paths=_files.ToArray();var buttons=new List<Button>();
        var search=new TextBox{Tag="tool-search",MaxLength=ToolSearchPolicy.MaximumQueryLength,ToolTip=global::Orbit.Core.Localization.L.Get("Text_C87D324DFDAA"),Margin=new Thickness(0,3,0,4)};_toolSearchBox=search;
        var caption=Text(global::Orbit.Core.Localization.L.Get("Text_582D863EDA45"),12);caption.SetResourceReference(TextBlock.ForegroundProperty,"TextBrush");_panelContent.Children.Add(caption);_panelContent.Children.Add(search);
        var line=new DockPanel{Margin=new Thickness(0,0,0,4)};var clear=Btn(global::Orbit.Core.Localization.L.Get("Text_8965271D3C97"),(_,_)=>{search.Clear();search.Focus();});clear.FontSize=11;DockPanel.SetDock(clear,Dock.Right);line.Children.Add(clear);
        var status=Text("",11);status.Tag="tool-search-status";status.SetResourceReference(TextBlock.ForegroundProperty,"GlassMutedText");line.Children.Add(status);_panelContent.Children.Add(line);
        var empty=Text(global::Orbit.Core.Localization.L.Get("Text_37460A9985B4"),12);empty.Tag="tool-search-empty";empty.SetResourceReference(TextBlock.ForegroundProperty,"TextBrush");_panelContent.Children.Add(empty);
        bool Current()=>!_closing&&panelVersion==_comparisonVersion&&loadVersion==_loadVersion&&paths.SequenceEqual(_files)&&_panel.Visibility==Visibility.Visible;
        void FocusAt(int index){if(buttons.Count>0){buttons[(index+buttons.Count)%buttons.Count].Focus();buttons[(index+buttons.Count)%buttons.Count].BringIntoView();}}
        void Render(){
            foreach(var button in buttons){button.IsEnabled=false;_panelContent.Children.Remove(button);}buttons.Clear();
            bool queried=ToolSearchPolicy.Normalize(search.Text).Length>0;
            foreach(var tool in tools.Where(t=>(queried||!t.SearchOnly)&&ToolSearchPolicy.Match(search.Text,t.Label,t.Keywords))){
                var button=Btn(tool.Label,(_,_)=>{});button.Tag="tool-command";button.Margin=new Thickness(0,4,0,4);button.ToolTip=tool.Label;
                button.Click+=(_,_)=>{if(!Current()||!buttons.Contains(button)){Toast(global::Orbit.Core.Localization.L.Get("Text_5858DE512520"));return;}tool.Run();};
                button.PreviewKeyDown+=(_,e)=>{if(Keyboard.Modifiers!=ModifierKeys.None)return;int i=buttons.IndexOf(button);if(i<0)return;if(e.Key==Key.Down){FocusAt(i+1);e.Handled=true;}else if(e.Key==Key.Up){if(i==0)search.Focus();else FocusAt(i-1);e.Handled=true;}};
                buttons.Add(button);_panelContent.Children.Add(button);
            }
            empty.Visibility=buttons.Count==0?Visibility.Visible:Visibility.Collapsed;status.Text=queried?global::Orbit.Core.Localization.L.Format("Text_B63F2FA7F898",(object?)(buttons.Count)):global::Orbit.Core.Localization.L.Format("Text_63465773E918",(object?)(buttons.Count));clear.IsEnabled=search.Text.Length>0;
        }
        search.TextChanged+=(_,_)=>{if(Current())Render();};
        search.PreviewKeyDown+=(_,e)=>{if(!Current()||Keyboard.Modifiers!=ModifierKeys.None)return;
            if(e.Key==Key.Down&&buttons.Count>0){FocusAt(0);e.Handled=true;}
            else if(e.Key==Key.Enter&&ToolSearchPolicy.Normalize(search.Text).Length>0){e.Handled=true;if(buttons.Count>0)buttons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
        };
        Render();_ = Dispatcher.InvokeAsync(()=>{if(Current())search.Focus();},DispatcherPriority.Input);
    }
}
