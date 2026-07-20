<div align="center">

# ⚡ NexusShell v4.0.0

### The Modern, Accessible, AI-Powered Windows Terminal
### الطرفية العصرية الذكية والمهيأة لقارئات الشاشة ونظام ويندوز

*A smarter way to work with PowerShell, CMD, and WSL — all in one beautiful, accessible interface.*
*طريقتك الأسرع والأكثر مرونة للعمل على PowerShell و CMD و WSL في واجهة واحدة متكاملة وسهلة الوصول.*

[![Platform](https://img.shields.io/badge/Platform-Windows-0078D6?style=flat-square&logo=windows)](https://github.com/Mohammedkhaled96/NexusShell)
[![License](https://img.shields.io/badge/License-MIT-green?style=flat-square)](LICENSE)
[![GitHub Issues](https://img.shields.io/github/issues/Mohammedkhaled96/NexusShell?style=flat-square)](https://github.com/Mohammedkhaled96/NexusShell/issues)

</div>

---

## 🚀 What's New in v4.0.0? / الجديد في الإصدار 4.0.0

Version 4.0.0 marks a revolutionary leap forward in performance, visual presentation, and accessibility. We have completely rewritten our core output engine to bridge the gap between traditional CLI tools and modern screen-reader interfaces.

يمثل الإصدار 4.0.0 قفزة ثورية في الأداء، العرض البصري، وسهولة الوصول لذوي الإعاقة البصرية والمحترفين. قمنا بإعادة بناء محرك المخرجات بالكامل لتقديم تجربة مثالية غير مسبوقة.

---

### 🌐 1. The WebView2 & xterm.js Revolution / الانتقال الثوري إلى WebView2
* **English:** We have replaced the traditional text control with an embedded Chromium host (`WebView2`). In the background, a hidden, high-performance `xterm.js` instance processes the raw shell (ConPTY) stream. This resolved text is then rendered into accessible HTML command blocks (`<h3>` headings for commands and `<pre role="log" aria-live="polite">` log regions).
* **العربية:** قمنا باستبدال شاشة عرض النصوص التقليدية بمحرك Chromium مدمج (`WebView2`). يعمل في الخلفية محاكي `xterm.js` عالي الأداء لتفكيك ومعالجة مجرى البيانات الصادر من النظام (ConPTY). يتم بعد ذلك عرض النصوص في كتل HTML برمجية مهيأة بالكامل لقارئات الشاشة (عنوان لكل أمر ومنطقة حية لقراءة المخرجات).
* **Benefit / الفائدة:** Absolute flexibility and ease of navigation for NVDA and JAWS users. Screen readers treat the terminal output as a clean web page, allowing line-by-line reading and heading navigation ('H').
مرونة كاملة لمستخدمي قارئات الشاشة (NVDA و JAWS) حيث يتم تصفح الطرفية كصفحة ويب منظمة وسهلة التصفح وقراءة الأسطر والانتقال بالعناوين.

### 🤖 2. Goodbye AI Filters (Zero Duplications) / التخلص النهائي من فلاتر الذكاء الاصطناعي والتكرار
* **English:** In previous versions, complex C# text-filtering logic was used to clean up AI outputs, which often resulted in duplicated lines, double-readings, and screen reader stuttering. In v4.0.0, the C# filter pipeline is completely removed. By leveraging `xterm.js` as a background parser, the terminal output is rendered clean, free from ANSI codes, stray backspaces, or double-reads.
* **العربية:** في الإصدارات السابقة، كان البرنامج يعتمد على فلاتر معقدة في C# لتصفية مخرجات الذكاء الاصطناعي، مما تسبب في حدوث تكرار مزعج للأسطر وقراءة مزدوجة من قارئ الشاشة. في الإصدار 4.0.0 تم التخلص تماماً من هذه الفلاتر؛ حيث يعالج محاكي `xterm.js` المخرجات برمجياً لتعرض خالية تماماً من رموز التحكم أو التكرارات المزعجة بصوت نقي ومباشر.

### 🎭 3. Fully Accessible Interactive Screen (TUIs) / الشاشة التفاعلية المتكاملة وسهلة الوصول
* **English:** Command-line interactive prompts (such as selecting models inside `agy /model` or answering prompts) are now fully intercepted. The WebView2 displays a beautiful, keyboard-accessible HTML `<dialog>` overlay. 
* **العربية:** أصبحت القوائم والخيارات التفاعلية لأدوات الطرفية (مثل قوائم `agy /model` أو أسئلة نعم/لا) تظهر تلقائياً في شاشة ويب تفاعلية مدمجة بصيغة `<dialog>` جميلة ومتوافقة برمجياً مع لوحة المفاتيح وقارئات الشاشة.
* **Navigation / التحكم:** Focus is automatically trapped inside the dialog. Use **Arrow Keys** to navigate, **Space/Enter** to confirm, and **Escape** to cancel and resume the terminal session.
يتم حصر تركيز لوحة المفاتيح داخل النافذة التفاعلية تلقائياً. استخدم **الأسهم** للتنقل، و **Space/Enter** للتأكيد، و **Escape** للإلغاء والعودة للطرفية.

### ✍️ 4. Intelligent Bilingual & RTL Alignment / دعم اللغات المشتركة ومحاذاة اليمين لليسار
* **English:** Traditional Windows consoles struggle with Right-to-Left (RTL) languages like Arabic, visually reversing words or breaking mixed English/Arabic layouts. NexusShell v4.0.0 implements per-line directionality checking (`dir="auto"`). Arabic and mixed lines are visually aligned to the right and ordered correctly for sighted users, while keeping the logical sequence untouched so screen readers pronounce them perfectly.
* **العربية:** تعاني طرفيات ويندوز من تشويه محاذاة اللغة العربية والكلمات الإنجليزية المتداخلة معها. في NexusShell v4.0.0 قمنا بتطبيق فحص الاتجاه التلقائي لكل سطر (`dir="auto"`)؛ حيث تظهر الجمل العربية والمختلطة محاذية لليمين ومرتبة بصرياً بشكل صحيح تماماً دون أي تغيير في النص البرمجي الأصلي الموجه لقارئ الشاشة.

### 🎵 5. Non-Intrusive Sound Effects / مؤثرات صوتية ذكية وجذابة
* **English:** Dynamic sound cues play on command execution, output updates, and opening/closing windows. These clicks and chimes are subtle, fast, and volume-scaled, providing valuable spatial awareness without interfering with speech synthesizers.
* **العربية:** تم إضافة مؤثرات صوتية ناعمة وجذابة عند تشغيل الأوامر، وتحديث المخرجات، أو فتح وإغلاق النوافذ، مما يمنح المستخدم مؤشرات صوتية مفيدة دون التأثير على صوت قارئ الشاشة أو مقاطعته.

---

## 📥 Installation / التثبيت

### Option 1 — Windows Package Manager (Recommended)
```powershell
winget install MF.NexusShell
```

### Option 2 — Manual Installer
1. Download **`NexusShell_Setup.exe`** from the [GitHub Releases Page](https://github.com/Mohammedkhaled96/NexusShell/releases).
2. Run the installer to set up the program, create desktop shortcuts, and optionally add it to your system PATH.
3. For silent installations, download **`NexusShell_Silent_Setup.exe`** which installs the app completely in the background.

---

## 🚀 Feature Overview / نظرة على مميزات البرنامج

* **Multi-Shell Support:** Run PowerShell, CMD, and WSL inside a unified interface.
* **Multi-Tab Sessions:** Open multiple independent tabs for different workflows.
* **Integrated AI Assistance (NexusAI):** Generate commands from natural language, explain terminal errors, and chat about your output using Groq's high-speed API.
* **SSH Manager:** Save and connect to remote servers with a single click.
* **Alias & Snippet Managers:** Eliminate repetitive typing with custom shortcuts and commands.
* **Environment Variable Editor:** Edit process variables in real-time.
* **Theme Manager:** Customize colors with instant live previews (Nexus Dark, Matrix, PowerShell Blue, Solarized).
* **Customizable Shortcut Manager:** Change all global hotkeys to match your personal preferences.

---

## ⌨️ Keyboard Shortcuts Reference / دليل مفاتيح الاختصار

| Shortcut / المفتاح | Action / الإجراء |
|----------|--------|
| `Enter` | Run command (Terminal) / Send message (AI Chat) |
| `F1` | Open Help / فتح دليل المساعدة |
| `F2 / Alt+S` | Open Settings / فتح الإعدادات |
| `F6 / Escape` | Focus Command Input box / نقل التركيز لمربع الكتابة |
| `Ctrl + T` | Open new terminal tab / فتح لسان طرفية جديد |
| `Ctrl + L` | Clear terminal output / مسح شاشة الطرفية |
| `Ctrl + Shift + S` | Stop running command / إيقاف الأمر الجاري تشغيله |
| `Ctrl + F` | Search terminal output / البحث في المخرجات |
| `Arrow Keys` | Navigate options (Interactive Dialog) / التنقل بالأسهم في شاشات الاختيار |
| `Space / Enter` | Select option (Interactive Dialog) / اختيار وتأكيد العنصر تفاعلياً |
| `Escape` | Dismiss dialog (Interactive Dialog) / إغلاق الحوار التفاعلي |

---

## 👥 Developers / فريق التطوير

* **Farid Mohammed** (Core Developer / مطور أساسي)
* **Mohammed Khaled** (Core Developer / مطور أساسي)

---

<div align="center">

**NexusShell — Making the terminal work for everyone.**
**ترمينال سهل الوصول ومناسب للجميع.**

</div>
