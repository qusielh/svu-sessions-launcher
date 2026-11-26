using System.ComponentModel;
using System.Globalization;
using System.Windows;

namespace SvuSessionsLauncher.Localization;

/// <summary>
/// Lightweight, live-switchable localisation service. Strings are keyed and
/// resolved through an indexer so XAML can bind to them and update instantly
/// when the language changes:
/// <c>{Binding [login.username], Source={x:Static loc:Loc.Instance}}</c>.
/// Arabic is rendered right-to-left via <see cref="FlowDirection"/>.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    private static readonly Dictionary<string, Dictionary<string, string>> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = new()
        {
            ["app.title"] = "SVU Online Session Launcher",
            ["app.subtitle"] = "Syrian Virtual University",

            ["login.title"] = "Sign in",
            ["login.windowTitle"] = "SVU Sign-in",
            ["login.subtitle"] = "Enter your university credentials to continue.",
            ["login.username"] = "Username",
            ["login.password"] = "Password",
            ["login.autoSignIn"] = "Sign me in automatically next time",
            ["login.privacy"] = "Your sign-in stays on this device only.",
            ["login.copyright"] = "\u00a9 2026 Qusie. MIT licensed.",
            ["login.username.placeholder"] = "e.g. name_123456",
            ["login.password.placeholder"] = "Your password",
            ["login.savePassword"] = "Remember my password",
            ["login.signIn"] = "Sign in",
            ["login.signingIn"] = "Signing in\u2026",
            ["login.showPassword"] = "Show password",
            ["login.error.empty"] = "Please enter your username and password.",
            ["login.error.invalid"] = "The username or password is incorrect.",
            ["login.error.tutorBlocked"] = "Tutor accounts are not supported in this student-only version yet.",
            ["login.error.network"] = "Could not reach the university service. Check your connection.",
            ["service.error"] = "The university service returned an error. Try again in a moment.",

            ["main.title"] = "Sessions",
            ["main.welcome"] = "Signed in as {0}",
            ["role.student"] = "Student",
            ["dataSource.live"] = "Live university service",
            ["main.options"] = "Show",
            ["opt.today"] = "Today's sessions",
            ["opt.myClasses"] = "My classes only",
            ["opt.myCourses"] = "All classes of my courses",
            ["opt.byProgram"] = "By programme and course",
            ["opt.otherClasses"] = "Other classes",
            ["main.program"] = "Programme",
            ["main.course"] = "Course",
            ["main.refresh"] = "Refresh",
            ["main.refreshing"] = "Refreshing\u2026",
            ["main.launch"] = "Launch session",
            ["main.launching"] = "Launching\u2026",
            ["main.logout"] = "Sign out",
            ["main.exit"] = "Exit",
            ["main.about"] = "About",
            ["main.selectProgram"] = "Select a programme",
            ["main.allPrograms"] = "All programmes",
            ["main.allCourses"] = "All courses",
            ["main.loadError"] = "Couldn't load sessions. Try again in a moment.",
            ["opt.mySessions"] = "My sessions (default)",
            ["main.selectCourse"] = "Select a course",
            ["main.selectSession"] = "Select a session from the list to launch it.",
            ["main.noSessions"] = "No sessions found for this course.",
            ["main.emptySessions"] = "No sessions to show right now.",
            ["main.loadingSessions"] = "Loading sessions\u2026",
            ["main.sessionCount"] = "{0} session(s)",
            ["main.endedJoinable"] = "This session has already ended \u2014 you can still join it.",
            ["main.cancelledJoinable"] = "This session was cancelled \u2014 you can still join it.",
            ["session.endedNetwork"] = "The session ended because the connection could not be recovered.",
            ["main.clockFormat"] = "12/24-hour clock",
            ["mic.muted"] = "Microphone: muted",
            ["mic.on"] = "Microphone: still on",
            ["mic.unknown"] = "Microphone: unknown",

            ["main.sessions"] = "Sessions",
            ["main.term"] = "Term",
            ["main.appearance"] = "Appearance",
            ["main.style"] = "Style",
            ["main.presets"] = "Preset themes",
            ["main.theme"] = "Theme",
            ["main.themeLight"] = "Light",
            ["main.themeDark"] = "Dark",
            ["main.globalColor"] = "Global colour",
            ["main.colorHint"] = "Pick any colour",
            ["main.labelInk"] = "Label ink",
            ["main.inkAuto"] = "Auto",
            ["main.inkBlack"] = "Black",
            ["main.inkWhite"] = "White",
            ["main.showMore"] = "Show more",
            ["main.showLess"] = "Show less",
            ["main.search"] = "Search by tutor or session\u2026",
            ["main.filterTutor"] = "Tutor\u2026",
            ["main.filterClass"] = "Class (C1)\u2026",
            ["main.noMatches"] = "No sessions match your filters.",
            ["select.search"] = "Type to find\u2026",
            ["select.empty"] = "No matches.",

            ["col.session"] = "Session",
            ["col.course"] = "Course",
            ["col.class"] = "Class",
            ["col.status"] = "Status",
            ["col.start"] = "Starts",
            ["col.tutor"] = "Tutor",
            ["col.server"] = "Server",

            ["status.available"] = "Available",
            ["status.inProgress"] = "In progress",
            ["status.finished"] = "Finished",
            ["status.cancelled"] = "Cancelled",
            ["status.unknown"] = "Unknown",

            ["about.title"] = "About",
            ["about.body"] = "A student-only desktop launcher for Syrian Virtual University online sessions. " +
                              "It brings session browsing, live rooms, chat, screen sharing, and recording downloads into one app. " +
                              "This is a beta release, so bugs and rough edges are expected. " +
                              "Please leave feedback or report issues on GitHub, and star the project if it helps you. " +
                              "Unofficial project, not affiliated with SVU.",
            ["about.support"] = "Leave feedback on GitHub",
            ["about.close"] = "Close",

            ["common.ok"] = "OK",
            ["common.cancel"] = "Cancel",
            ["common.retry"] = "Retry",
            ["common.language"] = "Language",

            ["diag.button"] = "Diagnostics",
            ["diag.running"] = "Running diagnostics\u2026",
            ["diag.done"] = "Diagnostics saved to {0}",
            ["diag.failed"] = "Diagnostics failed.",

            ["download.title"] = "Download sessions",
            ["download.subtitle"] = "Pick a term, then narrow down to a class to list its recordings.",
            ["download.saveTo"] = "Save to",
            ["download.changeFolder"] = "Change folder",
            ["download.select"] = "Select\u2026",
            ["download.term"] = "Term",
            ["download.program"] = "Program",
            ["download.course"] = "Course",
            ["download.tutor"] = "Tutor",
            ["download.class"] = "Class",
            ["download.colTerm"] = "Term",
            ["download.colDate"] = "Date",
            ["download.loading"] = "Loading sessions\u2026",
            ["download.empty"] = "No recordings were found for this class.",
            ["download.selectAll"] = "Select all",
            ["download.clear"] = "Clear",
            ["download.selected"] = "Selected: {0}",
            ["download.download"] = "Download",
            ["download.cancel"] = "Cancel",
            ["download.cancelling"] = "Cancelling\u2026",
            ["download.status.queued"] = "Queued",
            ["download.status.downloading"] = "Downloading",
            ["download.status.done"] = "Done",
            ["download.status.failed"] = "Failed",
            ["download.status.cancelled"] = "Cancelled",
            ["download.retrying"] = "Reconnecting\u2026",
            ["download.error"] = "Couldn't reach the server. Check your connection and try again.",
            ["download.error.noLink"] = "No recording link was returned for this session.",
            ["download.error.file"] = "The recording download failed.",
            ["download.modeQueue"] = "One by one",
            ["download.modeParallel"] = "All at once",
            ["download.done"] = "Downloaded {0} file(s).",
            ["download.doneSome"] = "Downloaded {0} file(s); {1} failed.",
            ["download.allClasses"] = "All classes",
            ["download.fanoutClass"] = "Class {0} of {1}\u2026",
            ["download.hint"] = "Select the filters above to list recordings."
        },
        ["ar"] = new()
        {
            ["app.title"] = "\u0645\u0634\u063a\u0644 \u0627\u0644\u062c\u0644\u0633\u0627\u062a \u0627\u0644\u0645\u062a\u0632\u0627\u0645\u0646\u0629",
            ["app.subtitle"] = "\u0627\u0644\u062c\u0627\u0645\u0639\u0629 \u0627\u0644\u0627\u0641\u062a\u0631\u0627\u0636\u064a\u0629 \u0627\u0644\u0633\u0648\u0631\u064a\u0629",

            ["login.title"] = "\u062a\u0633\u062c\u064a\u0644 \u0627\u0644\u062f\u062e\u0648\u0644",
            ["login.windowTitle"] = "تسجيل الدخول — SVU",
            ["login.subtitle"] = "\u0623\u062f\u062e\u0644 \u0628\u064a\u0627\u0646\u0627\u062a \u062d\u0633\u0627\u0628\u0643 \u0627\u0644\u062c\u0627\u0645\u0639\u064a \u0644\u0644\u0645\u062a\u0627\u0628\u0639\u0629.",
            ["login.username"] = "\u0627\u0633\u0645 \u0627\u0644\u0645\u0633\u062a\u062e\u062f\u0645",
            ["login.password"] = "\u0643\u0644\u0645\u0629 \u0627\u0644\u0645\u0631\u0648\u0631",
            ["login.autoSignIn"] = "\u0633\u062c\u0651\u0644 \u062f\u062e\u0648\u0644\u064a \u062a\u0644\u0642\u0627\u0626\u064a\u064b\u0627 \u0641\u064a \u0627\u0644\u0645\u0631\u0629 \u0627\u0644\u0642\u0627\u062f\u0645\u0629",
            ["login.privacy"] = "\u064a\u0628\u0642\u0649 \u062a\u0633\u062c\u064a\u0644 \u062f\u062e\u0648\u0644\u0643 \u0639\u0644\u0649 \u0647\u0630\u0627 \u0627\u0644\u062c\u0647\u0627\u0632",
            ["login.copyright"] = "\u00a9 2026 Qusie. \u0645\u0631\u062e\u0651\u0635 \u0628\u0645\u0648\u062c\u0628 MIT.",
            ["login.username.placeholder"] = "\u0645\u062b\u0627\u0644: name_123456",
            ["login.password.placeholder"] = "\u0643\u0644\u0645\u0629 \u0627\u0644\u0645\u0631\u0648\u0631",
            ["login.savePassword"] = "\u062a\u0630\u0643\u0651\u0631 \u0643\u0644\u0645\u0629 \u0627\u0644\u0645\u0631\u0648\u0631",
            ["login.signIn"] = "\u062f\u062e\u0648\u0644",
            ["login.signingIn"] = "\u062c\u0627\u0631\u064d \u062a\u0633\u062c\u064a\u0644 \u0627\u0644\u062f\u062e\u0648\u0644\u2026",
            ["login.showPassword"] = "\u0625\u0638\u0647\u0627\u0631 \u0643\u0644\u0645\u0629 \u0627\u0644\u0645\u0631\u0648\u0631",
            ["login.error.empty"] = "\u0627\u0644\u0631\u062c\u0627\u0621 \u0625\u062f\u062e\u0627\u0644 \u0627\u0633\u0645 \u0627\u0644\u0645\u0633\u062a\u062e\u062f\u0645 \u0648\u0643\u0644\u0645\u0629 \u0627\u0644\u0645\u0631\u0648\u0631.",
            ["login.error.invalid"] = "اسم المستخدم أو كلمة المرور غير صحيحة.",
            ["login.error.tutorBlocked"] = "\u062d\u0633\u0627\u0628\u0627\u062a \u0627\u0644\u0645\u062f\u0631\u0651\u0633\u064a\u0646 \u063a\u064a\u0631 \u0645\u062f\u0639\u0648\u0645\u0629 \u0641\u064a \u0647\u0630\u0647 \u0627\u0644\u0646\u0633\u062e\u0629 \u0627\u0644\u0645\u062e\u0635\u0651\u0635\u0629 \u0644\u0644\u0637\u0644\u0627\u0628 \u0628\u0639\u062f.",
            ["login.error.network"] = "\u062a\u0639\u0630\u0651\u0631 \u0627\u0644\u0648\u0635\u0648\u0644 \u0625\u0644\u0649 \u062e\u062f\u0645\u0629 \u0627\u0644\u062c\u0627\u0645\u0639\u0629. \u062a\u062d\u0642\u0651\u0642 \u0645\u0646 \u0627\u062a\u0635\u0627\u0644\u0643.",
            ["service.error"] = "\u0623\u0639\u0627\u062f\u062a \u062e\u062f\u0645\u0629 \u0627\u0644\u062c\u0627\u0645\u0639\u0629 \u062e\u0637\u0623\u064b. \u062d\u0627\u0648\u0644 \u0645\u062c\u062f\u062f\u064b\u0627 \u0628\u0639\u062f \u0642\u0644\u064a\u0644.",

            ["main.title"] = "\u0627\u0644\u062c\u0644\u0633\u0627\u062a",
            ["main.welcome"] = "\u0645\u0631\u062d\u0628\u064b\u0627 {0}",
            ["role.student"] = "\u0637\u0627\u0644\u0628",
            ["dataSource.live"] = "خدمة الجامعة المباشرة",
            ["main.options"] = "الخيارات",
            ["opt.today"] = "\u062c\u0644\u0633\u0627\u062a \u0627\u0644\u064a\u0648\u0645",
            ["opt.myClasses"] = "\u062d\u0635\u0635\u064a \u0641\u0642\u0637",
            ["opt.myCourses"] = "\u062c\u0645\u064a\u0639 \u0635\u0641\u0648\u0641 \u0645\u0648\u0627\u062f\u064a",
            ["opt.byProgram"] = "\u062d\u0633\u0628 \u0627\u0644\u0628\u0631\u0646\u0627\u0645\u062c \u0648\u0627\u0644\u0645\u0627\u062f\u0629",
            ["opt.otherClasses"] = "\u0634\u0639\u0628 \u0623\u062e\u0631\u0649",
            ["main.program"] = "\u0627\u0644\u0628\u0631\u0646\u0627\u0645\u062c",
            ["main.course"] = "\u0627\u0644\u0645\u0642\u0631\u0631",
            ["main.refresh"] = "\u062a\u062d\u062f\u064a\u062b",
            ["main.refreshing"] = "\u062c\u0627\u0631\u064d \u0627\u0644\u062a\u062d\u062f\u064a\u062b\u2026",
            ["main.launch"] = "\u0628\u062f\u0621 \u0627\u0644\u062c\u0644\u0633\u0629",
            ["main.launching"] = "\u062c\u0627\u0631\u064d \u0627\u0644\u0628\u062f\u0621\u2026",
            ["main.logout"] = "\u062a\u0633\u062c\u064a\u0644 \u0627\u0644\u062e\u0631\u0648\u062c",
            ["main.exit"] = "\u062e\u0631\u0648\u062c",
            ["main.about"] = "حول التطبيق",
            ["main.selectProgram"] = "\u0627\u062e\u062a\u0631 \u0628\u0631\u0646\u0627\u0645\u062c\u064b\u0627",
            ["main.allPrograms"] = "\u0643\u0644 \u0627\u0644\u0628\u0631\u0627\u0645\u062c",
            ["main.allCourses"] = "\u0643\u0644 \u0627\u0644\u0645\u0642\u0631\u0631\u0627\u062a",
            ["main.loadError"] = "\u062a\u0639\u0630\u0651\u0631 \u062a\u062d\u0645\u064a\u0644 \u0627\u0644\u062c\u0644\u0633\u0627\u062a. \u062d\u0627\u0648\u0644 \u0645\u062c\u062f\u062f\u064b\u0627 \u0628\u0639\u062f \u0642\u0644\u064a\u0644.",
            ["opt.mySessions"] = "\u062c\u0644\u0633\u0627\u062a\u064a (\u0627\u0644\u0627\u0641\u062a\u0631\u0627\u0636\u064a)",
            ["main.selectCourse"] = "\u0627\u062e\u062a\u0631 \u0645\u0642\u0631\u0631\u064b\u0627",
            ["main.selectSession"] = "\u0627\u062e\u062a\u0631 \u062c\u0644\u0633\u0629 \u0645\u0646 \u0627\u0644\u0642\u0627\u0626\u0645\u0629 \u0644\u0628\u062f\u0626\u0647\u0627.",
            ["main.noSessions"] = "\u0644\u0627 \u062a\u0648\u062c\u062f \u062c\u0644\u0633\u0627\u062a \u0644\u0647\u0630\u0627 \u0627\u0644\u0645\u0642\u0631\u0631.",
            ["main.emptySessions"] = "\u0644\u0627 \u062a\u0648\u062c\u062f \u062c\u0644\u0633\u0627\u062a \u0644\u0639\u0631\u0636\u0647\u0627 \u0627\u0644\u0622\u0646.",
            ["main.loadingSessions"] = "\u062c\u0627\u0631\u064d \u062a\u062d\u0645\u064a\u0644 \u0627\u0644\u062c\u0644\u0633\u0627\u062a\u2026",
            ["main.sessionCount"] = "{0} \u062c\u0644\u0633\u0629",
            ["main.endedJoinable"] = "\u0627\u0646\u062a\u0647\u062a \u0647\u0630\u0647 \u0627\u0644\u062c\u0644\u0633\u0629 \u2014 \u0644\u0627 \u064a\u0632\u0627\u0644 \u0628\u0625\u0645\u0643\u0627\u0646\u0643 \u0627\u0644\u0627\u0646\u0636\u0645\u0627\u0645 \u0625\u0644\u064a\u0647\u0627.",
            ["main.cancelledJoinable"] = "\u0623\u064f\u0644\u063a\u064a\u062a \u0647\u0630\u0647 \u0627\u0644\u062c\u0644\u0633\u0629 \u2014 \u0644\u0627 \u064a\u0632\u0627\u0644 \u0628\u0625\u0645\u0643\u0627\u0646\u0643 \u0627\u0644\u0627\u0646\u0636\u0645\u0627\u0645 \u0625\u0644\u064a\u0647\u0627.",
            ["session.endedNetwork"] = "\u0627\u0646\u062a\u0647\u062a \u0627\u0644\u062c\u0644\u0633\u0629 \u0644\u0623\u0646 \u0627\u0644\u0627\u062a\u0635\u0627\u0644 \u062a\u0639\u0630\u0651\u0631 \u0627\u0633\u062a\u0639\u0627\u062f\u062a\u0647.",
            ["main.clockFormat"] = "\u0635\u064a\u063a\u0629 \u0627\u0644\u0633\u0627\u0639\u0629 (12/24)",
            ["mic.muted"] = "الميكروفون: مكتوم",
            ["mic.on"] = "الميكروفون: ما زال مفتوحًا",
            ["mic.unknown"] = "الميكروفون: غير معروف",

            ["main.sessions"] = "\u0627\u0644\u062c\u0644\u0633\u0627\u062a",
            ["main.term"] = "\u0627\u0644\u0641\u0635\u0644",
            ["main.appearance"] = "\u0627\u0644\u0645\u0638\u0647\u0631",
            ["main.style"] = "\u0627\u0644\u0646\u0645\u0637 \u0627\u0644\u0639\u0627\u0645",
            ["main.presets"] = "\u0623\u0646\u0645\u0627\u0637 \u062c\u0627\u0647\u0632\u0629",
            ["main.theme"] = "\u0627\u0644\u0646\u0645\u0637",
            ["main.themeLight"] = "\u0641\u0627\u062a\u062d",
            ["main.themeDark"] = "\u062f\u0627\u0643\u0646",
            ["main.globalColor"] = "\u0627\u0644\u0644\u0648\u0646 \u0627\u0644\u0639\u0627\u0645",
            ["main.colorHint"] = "\u0627\u062e\u062a\u0631 \u0623\u064a \u0644\u0648\u0646",
            ["main.labelInk"] = "\u0644\u0648\u0646 \u0627\u0644\u0646\u0635\u0648\u0635",
            ["main.inkAuto"] = "\u062a\u0644\u0642\u0627\u0626\u064a",
            ["main.inkBlack"] = "\u0623\u0633\u0648\u062f",
            ["main.inkWhite"] = "\u0623\u0628\u064a\u0636",
            ["main.showMore"] = "\u0639\u0631\u0636 \u0627\u0644\u0645\u0632\u064a\u062f",
            ["main.showLess"] = "\u0639\u0631\u0636 \u0623\u0642\u0644",
            ["main.search"] = "\u0627\u0628\u062d\u062b \u0628\u0627\u0633\u0645 \u0627\u0644\u0645\u062f\u0631\u0651\u0633 \u0623\u0648 \u0627\u0644\u062c\u0644\u0633\u0629\u2026",
            ["main.filterTutor"] = "\u0627\u0644\u0645\u062f\u0631\u0651\u0633\u2026",
            ["main.filterClass"] = "\u0627\u0644\u0635\u0641 (C1)\u2026",
            ["main.noMatches"] = "\u0644\u0627 \u062a\u0648\u062c\u062f \u062c\u0644\u0633\u0627\u062a \u0645\u0637\u0627\u0628\u0642\u0629 \u0644\u0641\u0644\u062a\u0631\u0627\u062a\u0643.",
            ["select.search"] = "\u0627\u0628\u062d\u062b\u2026",
            ["select.empty"] = "\u0644\u0627 \u062a\u0648\u062c\u062f \u0646\u062a\u0627\u0626\u062c.",

            ["col.session"] = "\u0627\u0644\u062c\u0644\u0633\u0629",
            ["col.course"] = "\u0627\u0644\u0645\u0642\u0631\u0631",
            ["col.class"] = "\u0635\u0641",
            ["col.status"] = "\u0627\u0644\u062d\u0627\u0644\u0629",
            ["col.start"] = "\u064a\u0628\u062f\u0623",
            ["col.tutor"] = "\u0627\u0644\u0645\u062f\u0631\u0651\u0633",
            ["col.server"] = "\u0627\u0644\u062e\u0627\u062f\u0645",

            ["status.available"] = "\u0645\u062a\u0627\u062d\u0629",
            ["status.inProgress"] = "\u062c\u0627\u0631\u064a\u0629",
            ["status.finished"] = "\u0645\u0646\u062a\u0647\u064a\u0629",
            ["status.cancelled"] = "\u0645\u0644\u063a\u0627\u0629",
            ["status.unknown"] = "\u063a\u064a\u0631 \u0645\u0639\u0631\u0648\u0641",

            ["about.title"] = "\u062d\u0648\u0644",
            ["about.body"] = "\u0645\u0634\u063a\u0651\u0644 \u0633\u0637\u062d \u0645\u0643\u062a\u0628 \u0645\u062e\u0635\u0651\u0635 \u0644\u0644\u0637\u0644\u0627\u0628 \u0641\u0642\u0637 \u0644\u062c\u0644\u0633\u0627\u062a \u0627\u0644\u062c\u0627\u0645\u0639\u0629 \u0627\u0644\u0627\u0641\u062a\u0631\u0627\u0636\u064a\u0629 \u0627\u0644\u0633\u0648\u0631\u064a\u0629. \u064a\u062c\u0645\u0639 \u062a\u0635\u0641\u0651\u062d \u0627\u0644\u062c\u0644\u0633\u0627\u062a \u0648\u0627\u0644\u063a\u0631\u0641 \u0627\u0644\u0645\u0628\u0627\u0634\u0631\u0629 \u0648\u0627\u0644\u062f\u0631\u062f\u0634\u0629 \u0648\u0645\u0634\u0627\u0631\u0643\u0629 \u0627\u0644\u0634\u0627\u0634\u0629 \u0648\u062a\u0646\u0632\u064a\u0644 \u0627\u0644\u062a\u0633\u062c\u064a\u0644\u0627\u062a \u0641\u064a \u062a\u0637\u0628\u064a\u0642 \u0648\u0627\u062d\u062f. \u0647\u0630\u0647 \u0646\u0633\u062e\u0629 \u062a\u062c\u0631\u064a\u0628\u064a\u0629 (\u0628\u064a\u062a\u0627)\u060c \u0644\u0630\u0627 \u064a\u064f\u062a\u0648\u0642\u0651\u0639 \u0648\u062c\u0648\u062f \u0623\u062e\u0637\u0627\u0621 \u0623\u0648 \u0646\u0642\u0627\u0637 \u063a\u064a\u0631 \u0645\u0643\u062a\u0645\u0644\u0629. \u064a\u0631\u062c\u0649 \u062a\u0631\u0643 \u0627\u0644\u0645\u0644\u0627\u062d\u0638\u0627\u062a \u0623\u0648 \u0627\u0644\u0625\u0628\u0644\u0627\u063a \u0639\u0646 \u0627\u0644\u0645\u0634\u0643\u0644\u0627\u062a \u0639\u0644\u0649 GitHub\u060c \u0648\u0645\u0646\u062d \u0627\u0644\u0645\u0634\u0631\u0648\u0639 \u0646\u062c\u0645\u0629 \u0625\u0646 \u0643\u0627\u0646 \u0645\u0641\u064a\u062f\u064b\u0627 \u0644\u0643. \u0645\u0634\u0631\u0648\u0639 \u063a\u064a\u0631 \u0631\u0633\u0645\u064a \u0648\u063a\u064a\u0631 \u062a\u0627\u0628\u0639 \u0644\u0644\u062c\u0627\u0645\u0639\u0629 \u0627\u0644\u0627\u0641\u062a\u0631\u0627\u0636\u064a\u0629.",
            ["about.support"] = "\u0627\u062a\u0631\u0643 \u0645\u0644\u0627\u062d\u0638\u0627\u062a\u0643 \u0639\u0644\u0649 GitHub",
            ["about.close"] = "\u0625\u063a\u0644\u0627\u0642",

            ["common.ok"] = "\u0645\u0648\u0627\u0641\u0642",
            ["common.cancel"] = "\u0625\u0644\u063a\u0627\u0621",
            ["common.retry"] = "\u0625\u0639\u0627\u062f\u0629 \u0627\u0644\u0645\u062d\u0627\u0648\u0644\u0629",
            ["common.language"] = "\u0627\u0644\u0644\u063a\u0629",

            ["diag.button"] = "التشخيص",
            ["diag.running"] = "\u062c\u0627\u0631\u064d \u062a\u0634\u063a\u064a\u0644 \u0627\u0644\u062a\u0634\u062e\u064a\u0635\u2026",
            ["diag.done"] = "\u062a\u0645 \u062d\u0641\u0638 \u0627\u0644\u062a\u0634\u062e\u064a\u0635 \u0641\u064a {0}",
            ["diag.failed"] = "\u0641\u0634\u0644 \u0627\u0644\u062a\u0634\u062e\u064a\u0635.",

            ["download.title"] = "\u062a\u0646\u0632\u064a\u0644 \u0627\u0644\u062c\u0644\u0633\u0627\u062a",
            ["download.subtitle"] = "\u0627\u062e\u062a\u0631 \u0627\u0644\u0641\u0635\u0644 \u062b\u0645 \u0636\u064a\u0651\u0642 \u062d\u062a\u0649 \u0627\u0644\u0635\u0641 \u0644\u0639\u0631\u0636 \u062a\u0633\u062c\u064a\u0644\u0627\u062a\u0647.",
            ["download.saveTo"] = "\u0627\u0644\u062d\u0641\u0638 \u0641\u064a",
            ["download.changeFolder"] = "\u062a\u063a\u064a\u064a\u0631 \u0627\u0644\u0645\u062c\u0644\u062f",
            ["download.select"] = "\u0627\u062e\u062a\u0631\u2026",
            ["download.term"] = "\u0627\u0644\u0641\u0635\u0644",
            ["download.program"] = "\u0627\u0644\u0628\u0631\u0646\u0627\u0645\u062c",
            ["download.course"] = "\u0627\u0644\u0645\u0642\u0631\u0631",
            ["download.tutor"] = "\u0627\u0644\u0645\u062f\u0631\u0651\u0633",
            ["download.class"] = "\u0627\u0644\u0635\u0641",
            ["download.colTerm"] = "\u0627\u0644\u0641\u0635\u0644",
            ["download.colDate"] = "\u0627\u0644\u062a\u0627\u0631\u064a\u062e",
            ["download.loading"] = "\u062c\u0627\u0631\u064d \u062a\u062d\u0645\u064a\u0644 \u0627\u0644\u062c\u0644\u0633\u0627\u062a\u2026",
            ["download.empty"] = "\u0644\u0627 \u062a\u0648\u062c\u062f \u062a\u0633\u062c\u064a\u0644\u0627\u062a \u0644\u0647\u0630\u0627 \u0627\u0644\u0635\u0641.",
            ["download.selectAll"] = "\u062a\u062d\u062f\u064a\u062f \u0627\u0644\u0643\u0644",
            ["download.clear"] = "\u0645\u0633\u062d",
            ["download.selected"] = "\u0627\u0644\u0645\u062d\u062f\u062f: {0}",
            ["download.download"] = "\u062a\u0646\u0632\u064a\u0644",
            ["download.cancel"] = "\u0625\u0644\u063a\u0627\u0621",
            ["download.cancelling"] = "\u062c\u0627\u0631\u064d \u0627\u0644\u0625\u0644\u063a\u0627\u0621\u2026",
            ["download.status.queued"] = "\u0641\u064a \u0627\u0644\u0627\u0646\u062a\u0638\u0627\u0631",
            ["download.status.downloading"] = "\u062c\u0627\u0631\u064d \u0627\u0644\u062a\u0646\u0632\u064a\u0644",
            ["download.status.done"] = "\u062a\u0645",
            ["download.status.failed"] = "\u0641\u0634\u0644",
            ["download.status.cancelled"] = "\u0645\u0644\u063a\u0649",
            ["download.retrying"] = "\u0625\u0639\u0627\u062f\u0629 \u0627\u0644\u0627\u062a\u0635\u0627\u0644\u2026",
            ["download.error"] = "\u062a\u0639\u0630\u0651\u0631 \u0627\u0644\u0648\u0635\u0648\u0644 \u0625\u0644\u0649 \u0627\u0644\u062e\u0627\u062f\u0645. \u062a\u062d\u0642\u0651\u0642 \u0645\u0646 \u0627\u062a\u0635\u0627\u0644\u0643 \u0648\u062d\u0627\u0648\u0644 \u0645\u062c\u062f\u062f\u064b\u0627.",
            ["download.error.noLink"] = "\u0644\u0645 \u064a\u062a\u0645 \u0625\u0631\u062c\u0627\u0639 \u0631\u0627\u0628\u0637 \u0627\u0644\u062a\u0633\u062c\u064a\u0644 \u0644\u0647\u0630\u0647 \u0627\u0644\u062c\u0644\u0633\u0629.",
            ["download.error.file"] = "\u062a\u0639\u0630\u0631 \u062a\u0646\u0632\u064a\u0644 \u0627\u0644\u062a\u0633\u062c\u064a\u0644.",
            ["download.modeQueue"] = "\u0648\u0627\u062d\u062f\u064b\u0627 \u062a\u0644\u0648 \u0627\u0644\u0622\u062e\u0631",
            ["download.modeParallel"] = "\u0627\u0644\u0643\u0644 \u0645\u0631\u0629 \u0648\u0627\u062d\u062f\u0629",
            ["download.done"] = "\u062a\u0645 \u062a\u0646\u0632\u064a\u0644 {0} \u0645\u0644\u0641.",
            ["download.doneSome"] = "تم تنزيل {0} ملفًا، وفشل {1}.",
            ["download.allClasses"] = "\u0643\u0644 \u0627\u0644\u0635\u0641\u0648\u0641",
            ["download.fanoutClass"] = "\u0627\u0644\u0635\u0641 {0} \u0645\u0646 {1}\u2026",
            ["download.hint"] = "\u0627\u062e\u062a\u0631 \u0627\u0644\u0641\u0644\u0627\u062a\u0631 \u0623\u0639\u0644\u0627\u0647 \u0644\u0639\u0631\u0636 \u0627\u0644\u062a\u0633\u062c\u064a\u0644\u0627\u062a."
        }
    };

    private string _language = "ar";
    private CultureInfo _culture;

    private Loc()
    {
        _culture = BuildCulture(_language);
        ApplyCulture(_culture);
    }

    /// <summary>
    /// Builds a writable culture for <paramref name="language"/> that keeps
    /// Latin (Western) digits everywhere, so dates and counts stay readable in
    /// both languages (e.g. Arabic shows Arabic month names but "12", "16:00").
    /// </summary>
    private static CultureInfo BuildCulture(string language)
    {
        var culture = (CultureInfo)CultureInfo.GetCultureInfo(language).Clone();
        if (culture.DateTimeFormat.Calendar is not GregorianCalendar)
        {
            culture.DateTimeFormat.Calendar = new GregorianCalendar();
        }

        culture.NumberFormat.NativeDigits = ["0", "1", "2", "3", "4", "5", "6", "7", "8", "9"];
        culture.NumberFormat.DigitSubstitution = DigitShapes.None;
        return culture;
    }

    private static void ApplyCulture(CultureInfo culture)
    {
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised after the active language changes.</summary>
    public static event Action? LanguageChanged;

    public string Language
    {
        get => _language;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || !Map.ContainsKey(value) || value == _language)
            {
                return;
            }

            _language = value;
            _culture = BuildCulture(value);
            ApplyCulture(_culture);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FlowDirection)));
            LanguageChanged?.Invoke();
        }
    }

    /// <summary>Gets a localised string, or the key itself if missing.</summary>
    public string this[string key] =>
        Map.TryGetValue(_language, out var table) && table.TryGetValue(key, out var value)
            ? value
            : key;

    /// <summary>Gets a formatted localised string.</summary>
    public string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, this[key], args);

    public FlowDirection FlowDirection =>
        _culture.TextInfo.IsRightToLeft
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;

    public bool IsRightToLeft => FlowDirection == FlowDirection.RightToLeft;

    /// <summary>Culture used for formatting dates and numbers (Latin digits).</summary>
    public CultureInfo Culture => _culture;

    /// <summary>Picks a sensible starting language from the OS UI culture.</summary>
    public string DetectDefaultLanguage() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is "ar" ? "ar" : "en";
}
