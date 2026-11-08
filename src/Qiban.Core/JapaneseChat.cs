using System.Text;

namespace Qiban.Core;

/// <summary>Japanese speech uses its original text, independently of the displayed translation.</summary>
public static class JapaneseChat
{
    internal const string Instruction = "当前启用日语聊天模式。用户可以使用任何语言输入，你必须理解其原意，始终只用自然日语回复。" +
        "保留人物性格、亲疏关系、语气、口头禅与说话节奏；人物设定、历史消息或用户要求中的其他输出语言不能改变此模式。" +
        "只输出可直接朗读的日语正文，不附中文、英文、罗马音、翻译、语言标签或解释。外语名称尽量使用常见日语或片假名读法。";

    public static bool IsJapaneseDisplay(string language) => language.Trim().ToLowerInvariant() is
        "日本語" or "日语" or "日文" or "日語" or "japanese" or "ja" or "ja-jp";

    // A conservative preflight, not a full linguistic classifier. Every prose sentence/line must
    // contain kana; otherwise a Japanese greeting could conceal a following Chinese paragraph.
    // Foreign names must use Japanese spelling. Pure kanji phrases remain ambiguous with Chinese.
    public static void EnsureJapanese(string text)
    {
        bool anyKana = false, segmentKana = false, segmentLetters = false;
        foreach (var rune in text.EnumerateRunes())
        {
            int c = rune.Value;
            if (c is '\n' or '\r' or '.' or '!' or '?' or '。' or '！' or '？' or ';' or '；')
            {
                CheckSegment(); segmentKana = false; segmentLetters = false;
                continue;
            }
            bool kana = c is >= 0x3041 and <= 0x3096 or >= 0x309d and <= 0x309f
                or >= 0x30a1 and <= 0x30fa or >= 0x30fd and <= 0x30ff
                or >= 0x31f0 and <= 0x31ff or >= 0xff66 and <= 0xff9d
                or >= 0x1aff0 and <= 0x1afff or >= 0x1b000 and <= 0x1b16f;
            bool japaneseLetter = kana || c is 0x3005 or 0x3006 or 0x303b or 0x30fc or 0xff70
                or >= 0x3400 and <= 0x4dbf or >= 0x4e00 and <= 0x9fff or >= 0xf900 and <= 0xfaff
                or >= 0x20000 and <= 0x2fa1f or >= 0x30000 and <= 0x323af;
            if (Rune.IsLetter(rune))
            {
                if (!japaneseLetter) throw Invalid();
                segmentLetters = true;
            }
            anyKana |= kana; segmentKana |= kana;
        }
        CheckSegment();
        if (!anyKana) throw Invalid();

        void CheckSegment() { if (segmentLetters && !segmentKana) throw Invalid(); }
        static InvalidDataException Invalid() => new("回复包含无法确认的日语内容，请重试或检查对话模型。");
    }
}
