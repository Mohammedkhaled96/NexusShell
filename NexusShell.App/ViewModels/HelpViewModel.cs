using NexusShell.App.Commands;
using System.Windows.Input;

namespace NexusShell.App.ViewModels
{
    public class HelpViewModel : ViewModelBase
    {
        public string HelpText => GetHelpText();

        public HelpViewModel()
        {
        }

        private string GetHelpText()
        {
            return
"NexusShell - Professional Accessible Terminal\n" +
"Detailed Reference Manual Guide\n" +
"==================================================\n" +
"\n" +
"1. INTRODUCTION\n" +
"--------------------------------------------------\n" +
"Welcome to NexusShell. This application is a high-performance\n" +
"terminal environment built for developers and power users, with\n" +
"native support for accessibility. It provides a seamless interface\n" +
"for PowerShell, CMD, and WSL with integrated AI assistance.\n" +
"\n" +
"Command output is rendered as clean, accurate text inside accessible\n" +
"command blocks (a heading for each command and a live region for its\n" +
"output), so NVDA and JAWS read results naturally - with no duplicated\n" +
"lines or stray control characters.\n" +
"\n" +
"2. GETTING STARTED: AI API KEY SETUP\n" +
"--------------------------------------------------\n" +
"To unlock the AI features (Generation, Summarization, Analysis),\n" +
"you must provide an API Key from Groq Cloud:\n" +
"\n" +
"1. GET KEY: Visit https://console.groq.com/keys and sign up.\n" +
"2. LOGIN: You can 'Continue with Google' or use your email.\n" +
"3. CREATE: Go to 'API Keys' in the sidebar, click 'Create API Key',\n" +
"   name it 'NexusShell', and COPY the resulting key immediately.\n" +
"4. ACTIVATE: Open Settings (Alt+S) -> Select 'AI' category -> Paste\n" +
"   your key in the 'Groq API Key' box -> Click 'Save Settings'.\n" +
"5. STATUS: If the status dot turns green, the AI is ready.\n" +
"\n" +
"3. DETAILED MENU BREAKDOWN (ALT KEY NAVIGATION)\n" +
"--------------------------------------------------\n" +
"\n" +
"[ FILE MENU (Alt + F) ]\n" +
"- Open Script: Select and run .ps1, .bat, or .cmd files directly.\n" +
"- Clear Terminal: Wipes the current terminal screen and history.\n" +
"- Execute / Stop: Runs the current input or stops a running process.\n" +
"- Export Command History: Saves your session commands to a text file.\n" +
"\n" +
"[ SETTINGS MENU (Alt + S) ]\n" +
"- General: Set default shell and terminal display modes.\n" +
"- Appearance: Adjust font families, sizes, and colors.\n" +
"- Integration: Toggle startup options and Windows context menu.\n" +
"- AI Settings: Manage API keys, models, and response language.\n" +
"\n" +
"[ MANAGERS MENU (Alt + M) ]\n" +
"- Shortcut Manager: Customize all keyboard hotkeys.\n" +
"- Snippet Manager: Store and reuse complex command blocks.\n" +
"- Alias Manager: Create short triggers for long commands.\n" +
"- Theme Manager: Change the visual styling of the terminal.\n" +
"- SSH Manager: Manage remote server connections securely.\n" +
"- Environment Variables: Edit system and user variables directly.\n" +
"- API Tester: Full Postman-style HTTP client (see section 4).\n" +
"\n" +
"[ SHELL MENU (Alt + H) ]\n" +
"- Dynamic Profiles: This menu lists all available shell profiles\n" +
"  (PowerShell, CMD, WSL). Selecting one opens a new terminal tab\n" +
"  using that specific environment.\n" +
"\n" +
"[ AI ASSISTANCE MENU (Alt + A) ]\n" +
"- Ask AI for Command: Generate raw shell commands from plain text.\n" +
"- Explain Output: Technical breakdown of terminal results.\n" +
"- Summarize Output: Actionable bullet points of long logs.\n" +
"- Clean Output: Removes noise for perfect screen reader clarity.\n" +
"- Chat About Output: Discussion focused on your current data.\n" +
"\n" +
"[ VIEW MENU (Alt + V) ]\n" +
"- Find: Search for text within the terminal output history.\n" +
"- Always on Top: Keeps the terminal window above all others.\n" +
"\n" +
"[ HELP MENU (Alt + H) ]\n" +
"- View Help: Opens this comprehensive reference guide (F1).\n" +
"- Check for Updates: Connect to GitHub to find new versions.\n" +
"- Support: Opens the contact form for direct developer help.\n" +
"- Go to GitHub: Visit the project repository for source/issues.\n" +
"- About: View version information and developer credits.\n" +
"\n" +
"4. API TESTER MODULE (Managers -> API Tester)\n" +
"--------------------------------------------------\n" +
"A built-in Postman-style HTTP client for designing, sending and\n" +
"organising REST requests — entirely inside NexusShell.\n" +
"\n" +
"[ Toolbar (top of window) ]\n" +
"- Environment selector: Pick the active set of {{variables}} that\n" +
"  will be substituted into URL, headers, body and auth fields.\n" +
"- HTTP Method dropdown: GET, POST, PUT, PATCH, DELETE, HEAD, OPTIONS\n" +
"  (color-coded by verb).\n" +
"- URL field: Supports {{variableName}} placeholders. Press\n" +
"  Ctrl+Enter anywhere to send.\n" +
"- SEND / CANCEL: Send fires the request; Cancel (Esc) aborts an\n" +
"  in-flight call.\n" +
"- SAVE (Ctrl+S): Persists the current request back into its\n" +
"  collection / folder.\n" +
"\n" +
"[ Sidebar - Collections tab ]\n" +
"- + New: Create a new collection.\n" +
"- Import: Load Postman v2.1 JSON or OpenAPI 3.x (.json/.yaml).\n" +
"- Tree view: Collections > Folders > Requests, fully nestable.\n" +
"- Actions menu (the blue dotted button on every row):\n" +
"  * Collection : Add Request, Add Folder, Rename, Export, Delete\n" +
"  * Folder     : Add Request, Add Subfolder, Rename, Delete\n" +
"  * Request    : Load, Rename, Duplicate, Delete\n" +
"  Right-click and the Apps / Shift+F10 keys open the same menu.\n" +
"\n" +
"[ Sidebar - History tab ]\n" +
"- The 100 most recent calls (method, status, URL, timestamp).\n" +
"- Click any entry to reload that exact request into the editor.\n" +
"- Clear History wipes the saved log.\n" +
"\n" +
"[ Request Builder Tabs ]\n" +
"- Params  : Query string key/value table with enable toggles.\n" +
"- Headers : Request header table; Content-Type is auto-suggested\n" +
"            when you switch body type.\n" +
"- Auth    : None, Bearer Token, Basic, or API Key (Header / Query).\n" +
"- Body    : Five body modes —\n" +
"            * none   : no body sent.\n" +
"            * JSON   : raw JSON editor with LIVE validation\n" +
"                      ('Valid JSON' / 'Invalid JSON' indicator)\n" +
"                      and a one-click Format button.\n" +
"            * raw    : raw editor for Text, XML or HTML (format\n" +
"                      dropdown selects the type).\n" +
"            * form-data           : multipart key/value table.\n" +
"            * x-www-form-urlencoded: classic form-encoded table.\n" +
"- Pre-request : JS-style script run before the request fires.\n" +
"- Tests       : Assertion script run against the response.\n" +
"- Code        : Auto-generated snippets for cURL, C#, JavaScript,\n" +
"                Python and TypeScript. Refresh + one-click Copy.\n" +
"\n" +
"[ Inline Response Panel (bottom) ]\n" +
"- Status header: HTTP code, status text, response time, payload size.\n" +
"- Body / Headers / Cookies tabs with read-only viewers.\n" +
"- Copy Body, Clear, and 'Full View' (opens a maximised window for\n" +
"  large payloads).\n" +
"- JSON responses are pretty-printed automatically.\n" +
"\n" +
"[ Persistence ]\n" +
"- Collections, environments and history are saved to:\n" +
"    %APPDATA%\\NexusShell\\ApiTester\\\n" +
"  No external account or cloud sync required.\n" +
"\n" +
"[ API Tester Keyboard Shortcuts ]\n" +
"Ctrl + Enter   : Send the current request\n" +
"Esc            : Cancel the in-flight request\n" +
"Ctrl + S       : Save the active request to its collection\n" +
"Apps / Sh+F10  : Open the actions menu for the focused tree row\n" +
"\n" +
"5. ACCESSIBILITY & SCREEN READERS (NVDA / JAWS)\n" +
"--------------------------------------------------\n" +
"NexusShell is optimized for all major screen readers:\n" +
"\n" +
"[ Terminal Area ]\n" +
"- Output is presented as accessible command blocks: each command is\n" +
"  a heading, and its result is a live region beneath it.\n" +
"- Your screen reader treats the output as a web document (browse mode):\n" +
"  press 'H' to jump between commands by heading, and use arrow keys to\n" +
"  read the output line by line.\n" +
"- New output is announced politely and does not interrupt your reading.\n" +
"- Type commands in the Command Input box and press Enter to run them;\n" +
"  press F6 or Escape to return focus to the input box at any time.\n" +
"\n" +
"[ Interactive Screens / TUIs ]\n" +
"- When interactive menus are detected (e.g. running 'agy /model' or selecting options),\n" +
"  an accessible HTML dialog overlay opens automatically, containing form controls.\n" +
"- Screen readers are automatically trapped inside this dialog to easily navigate options.\n" +
"- Use Arrow keys to select option items, and Space/Enter to activate. Press Escape to close.\n" +
"\n" +
"[ Bilingual & RTL Support ]\n" +
"- Terminal output lines are dynamically processed for Directionality (dir=\"auto\").\n" +
"- Arabic and mixed Arabic/English lines are rendered from Right-to-Left (RTL) visually\n" +
"  with correct word ordering, while preserving logical text sequences for screen readers.\n" +
"\n" +
"[ AI Chat Interface ]\n" +
"- BROWSE MODE: Both NVDA and JAWS will treat the chat as a web page.\n" +
"- NAVIGATION: Press 'H' to jump by headings (Speakers) and 'B' to\n" +
"  jump between 'Copy Message' buttons.\n" +
"- SMART FOCUS: When returning from the input box via Shift+Tab,\n" +
"  your screen reader returns to the exact line you were last reading.\n" +
"\n" +
"[ API Tester ]\n" +
"- Every control carries an AutomationProperties.Name so screen\n" +
"  readers announce its purpose (e.g. 'HTTP method', 'Send request').\n" +
"- The response status panel uses a Polite live region — your reader\n" +
"  announces new status codes without interrupting current speech.\n" +
"- JSON validation status is announced live as you type.\n" +
"\n" +
"6. KEYBOARD SHORTCUTS REFERENCE\n" +
"--------------------------------------------------\n" +
"F1                : Open Help documentation\n" +
"F2 / Alt+S        : Open Settings categories\n" +
"F6 / Escape       : Return focus to Command Input box\n" +
"Enter             : Run command (Terminal) / Send message (AI Chat)\n" +
"Shift + Enter     : New line in AI Chat input\n" +
"Ctrl + T          : Open a new terminal tab\n" +
"Ctrl + L          : Clear terminal screen\n" +
"Ctrl + Shift + S  : Toggle Execute / Stop command\n" +
"Ctrl + Enter      : (API Tester) Send request\n" +
"Ctrl + S          : (API Tester) Save active request to collection\n" +
"Esc               : (API Tester) Cancel an in-flight request\n" +
"Arrow Keys        : (Interactive Dialog) Navigate options\n" +
"Space / Enter     : (Interactive Dialog) Select & confirm option\n" +
"Escape            : (Interactive Dialog) Cancel and dismiss dialog\n" +
"\n" +
"7. SUPPORT & CREDITS\n" +
"--------------------------------------------------\n" +
"Developers: Mohammed Khaled & Farid Mohammed\n" +
"Version 4.0.0 - Built for Performance and Accessibility.\n" +
"Thank you for using NexusShell!\n";
        }
    }
}
