using System.Collections.Generic;

namespace ImageViewer.Gallery;

/// <summary>原生 WPF 窗口（极速查看器 Flash）的本地化：读 SettingsStore 保存的语言，
/// 返回该语言下的 UI 文本。zh-CN 直接返回中文原文（key）。其余语言各维护一个词典。
/// 与前端 js/i18n.js 的翻译保持一致（key 用中文原文，便于对照）。</summary>
public static class Loc
{
    /// <summary>取 key 在指定语言下的文本；未命中或 lang 为空时返回中文原文。</summary>
    public static string T(string key, string lang)
    {
        lang = (lang ?? "").Trim().ToLowerInvariant();
        return lang switch
        {
            "en" => _en.GetValueOrDefault(key, key),
            "zh-tw" => _zhTw.GetValueOrDefault(key, key),
            "ja" => _ja.GetValueOrDefault(key, key),
            "ko" => _ko.GetValueOrDefault(key, key),
            "ru" => _ru.GetValueOrDefault(key, key),
            "fr" => _fr.GetValueOrDefault(key, key),
            "de" => _de.GetValueOrDefault(key, key),
            _ => key,
        };
    }

    private static readonly Dictionary<string, string> _en = new()
    {
        ["回到相册"] = "Back to Albums",
        ["适应窗口"] = "Fit to window",
        ["缩小"] = "Zoom out",
        ["放大"] = "Zoom in",
        ["左旋 90°"] = "Rotate left 90°",
        ["右旋 90°"] = "Rotate right 90°",
        ["切换背景（灰 / 白）"] = "Toggle background (gray / white)",
        ["拖入图片以快速查看"] = "Drop an image to view",
        ["无法打开图片"] = "Unable to open image",
        ["打开主界面"] = "Open Main Interface",
        ["Flash 查看器"] = "Flash Viewer",
        ["退出"] = "Exit",
    };

    private static readonly Dictionary<string, string> _zhTw = new()
    {
        ["回到相册"] = "回到相簿",
        ["适应窗口"] = "適合視窗",
        ["缩小"] = "縮小",
        ["放大"] = "放大",
        ["左旋 90°"] = "左旋 90°",
        ["右旋 90°"] = "右旋 90°",
        ["切换背景（灰 / 白）"] = "切換背景（灰 / 白）",
        ["拖入图片以快速查看"] = "拖入圖片以快速檢視",
        ["无法打开图片"] = "無法開啟圖片",
        ["打开主界面"] = "開啟主介面",
        ["Flash 查看器"] = "Flash 檢視器",
        ["退出"] = "結束",
    };

    private static readonly Dictionary<string, string> _ja = new()
    {
        ["回到相册"] = "アルバムに戻る",
        ["适应窗口"] = "ウィンドウに合わせる",
        ["缩小"] = "縮小",
        ["放大"] = "拡大",
        ["左旋 90°"] = "左へ 90°",
        ["右旋 90°"] = "右へ 90°",
        ["切换背景（灰 / 白）"] = "背景切替（グレー / 白）",
        ["拖入图片以快速查看"] = "画像をドロップして表示",
        ["无法打开图片"] = "画像を開けません",
        ["打开主界面"] = "メイン画面を開く",
        ["Flash 查看器"] = "Flash ビューア",
        ["退出"] = "終了",
    };

    private static readonly Dictionary<string, string> _ko = new()
    {
        ["回到相册"] = "앨범으로 돌아가기",
        ["适应窗口"] = "창에 맞추기",
        ["缩小"] = "축소",
        ["放大"] = "확대",
        ["左旋 90°"] = "왼쪽 90°",
        ["右旋 90°"] = "오른쪽 90°",
        ["切换背景（灰 / 白）"] = "배경 전환 (회색 / 흰색)",
        ["拖入图片以快速查看"] = "이미지로 드래그하여 보기",
        ["无法打开图片"] = "이미지를 열 수 없습니다",
        ["打开主界面"] = "메인 화면 열기",
        ["Flash 查看器"] = "Flash 뷰어",
        ["退出"] = "종료",
    };

    private static readonly Dictionary<string, string> _ru = new()
    {
        ["回到相册"] = "К альбомам",
        ["适应窗口"] = "По размеру окна",
        ["缩小"] = "Уменьшить",
        ["放大"] = "Увеличить",
        ["左旋 90°"] = "Повернуть влево 90°",
        ["右旋 90°"] = "Повернуть вправо 90°",
        ["切换背景（灰 / 白）"] = "Переключить фон (серый / белый)",
        ["拖入图片以快速查看"] = "Перетащите изображение для просмотра",
        ["无法打开图片"] = "Не удаётся открыть изображение",
        ["打开主界面"] = "Открыть главный интерфейс",
        ["Flash 查看器"] = "Просмотрщик Flash",
        ["退出"] = "Выход",
    };

    private static readonly Dictionary<string, string> _fr = new()
    {
        ["回到相册"] = "Retour aux albums",
        ["适应窗口"] = "Ajuster à la fenêtre",
        ["缩小"] = "Réduire",
        ["放大"] = "Agrandir",
        ["左旋 90°"] = "Rotation gauche 90°",
        ["右旋 90°"] = "Rotation droite 90°",
        ["切换背景（灰 / 白）"] = "Basculer l'arrière-plan (gris / blanc)",
        ["拖入图片以快速查看"] = "Déposez une image pour voir",
        ["无法打开图片"] = "Impossible d'ouvrir l'image",
        ["打开主界面"] = "Ouvrir l'interface principale",
        ["Flash 查看器"] = "Visionneuse Flash",
        ["退出"] = "Quitter",
    };

    private static readonly Dictionary<string, string> _de = new()
    {
        ["回到相册"] = "Zurück zu Alben",
        ["适应窗口"] = "An Fenster anpassen",
        ["缩小"] = "Verkleinern",
        ["放大"] = "Vergrößern",
        ["左旋 90°"] = "Links drehen 90°",
        ["右旋 90°"] = "Rechts drehen 90°",
        ["切换背景（灰 / 白）"] = "Hintergrund wechseln (grau / weiß)",
        ["拖入图片以快速查看"] = "Bild hineinziehen zum Anzeigen",
        ["无法打开图片"] = "Bild konnte nicht geöffnet werden",
        ["打开主界面"] = "Hauptoberfläche öffnen",
        ["Flash 查看器"] = "Flash-Betrachter",
        ["退出"] = "Beenden",
    };
}
