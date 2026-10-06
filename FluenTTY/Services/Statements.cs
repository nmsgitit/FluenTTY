namespace FluenTTY.Services;

/// <summary>Static text shown in About: the AI-assistance disclosure and the privacy statement.</summary>
public static class Statements
{
    public const string AiDisclosureTitle = "AI-assisted development disclosure";

    public const string AiDisclosure =
"""
Portions of this software were developed with the assistance of artificial-intelligence tools. In particular, AI coding assistants (Claude by Anthropic, used through Claude Code) were used to generate, refactor, review and document source code, and to draft documentation and user-facing text.

All AI-assisted output has been reviewed, tested and integrated by the project's human maintainers, who take full responsibility for the software and its behaviour. The use of AI tools does not alter the licensing of this software: FluenTTY is released under the MIT License, and third-party components remain under their respective licenses as listed under Licenses.

FluenTTY's settings and protocol behaviour were modelled on PuTTY 0.85 (MIT License), whose source code was used as a reference; no PuTTY code is compiled into FluenTTY. No AI-generated content in this project is knowingly derived from proprietary or non-permissively licensed material. If you believe any part of this software infringes your rights, please contact the maintainers so it can be reviewed and corrected.

This application itself contains no AI features. It does not connect to any AI service and does not send your sessions, files, keystrokes or usage to any third party.
""";

    public const string Privacy =
"""
FluenTTY connects to the servers and devices you choose, and to GitHub to check for updates. It collects no telemetry, analytics or crash reports.

Each time it starts, FluenTTY asks GitHub (github.com, where FluenTTY is published) for the number of the latest version. As with any web request, GitHub sees your IP address and that a copy of FluenTTY is asking, including its version number. Nothing about you, your sessions, files or settings is sent. A new version is downloaded from GitHub only when you choose to install it. You can turn update checks off in Settings › Updates.

Settings, saved sessions and accepted host keys are stored in the FluenTTY data folder shown below and nowhere else. Saved passwords are encrypted with Windows Data Protection for your Windows account; they are only written to an export file if you choose to include them, protected with a passphrase you pick. Session logs are written only when you turn logging on.
""";
}
