# 🐙 OctoFetch - GitHub Cloud Leecher

[![Platform](https://img.shields.io/badge/Platform-Windows-blue.svg)](https://microsoft.com/windows)
[![Framework](https://img.shields.io/badge/Framework-.NET_8.0-purple.svg)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

[🇬🇧 English](#-english) | [🇮🇷 فارسی](#-فارسی)

---

<a name="english"></a>
## 🇬🇧 English

### Overview
**OctoFetch** is a lightweight, multi-node desktop application designed to bypass local network restrictions and bandwidth limits by utilizing **GitHub Actions** as a cloud download engine. 

Instead of downloading large files directly to your local machine using your own bandwidth, OctoFetch sends the file URL to a GitHub virtual environment. The GitHub server downloads the file at high speeds, splits it into chunks (to bypass GitHub's file size limits), and commits them to your private repository. You can then download or share the raw GitHub links.

### Key Features

| Feature | Description |
| :--- | :--- |
| **Multi-Node Clustering** | Add multiple GitHub Personal Access Tokens (PATs). The app uses a Round-Robin load balancer to distribute download tasks across different accounts, preventing rate limits. |
| **Pure GitHub Engine** | Completely reliant on GitHub Actions. No third-party cloud integrations (like Google Drive) are used, ensuring a highly stable and unblockable architecture. |
| **Aggregated File Manager** | View, copy links, and delete files across *all* connected GitHub repositories seamlessly in one unified interface. |
| **Smart Local Extractor** | A built-in tool to automatically reassemble and extract the downloaded multi-part ZIP files (`.zip.001`, `.zip.002`) on your local machine. |
| **Privacy & Security** | Toggle repositories between Public and Private directly from the app. Includes an option to hash/encrypt filenames before uploading. |

### How It Works
1. **Trigger:** You input a direct download URL into OctoFetch.
2. **Action Dispatch:** The app sends a secure workflow dispatch to one of your connected GitHub repositories.
3. **Cloud Processing:** A GitHub Ubuntu server downloads the target file.
4. **Chunking:** Since GitHub limits individual file sizes, the server automatically archives the file and splits it into 90MB chunks.
5. **Retrieval:** The chunks are committed to a `downloads/` folder in your repo. OctoFetch fetches the raw URLs for you to download.

### Prerequisites
* Windows OS
* A GitHub Account
* A GitHub **Personal Access Token (PAT)** (Fine-grained) with the `repo` and `workflow` scopes selected.

### Usage Instructions
1. Navigate to the **⚙️ Node Management** tab.
2. Enter your GitHub PAT and a Repository Name (e.g., `Cloud-Downloads`), then click **Add Node**.
3. Click **Test All Nodes** to verify connections and automatically inject the necessary workflow files into your repositories.
4. Go to the **🚀 Dashboard**, paste a direct URL, and click **Start Cloud Transfer**.

---

<a name="persian"></a>
## 🇮🇷 فارسی

### معرفی
نرم‌افزار دسکتاپ سبک و چند-نودی **OctoFetch** با استفاده از **GitHub Actions** به عنوان یک موتور دانلود ابری، محدودیت‌های شبکه محلی و پهنای باند را دور می‌زند.

به جای اینکه فایل‌های حجیم را مستقیماً و با اینترنت شخصی خود دانلود کنید، اکتافچ لینک فایل را به سرورهای مجازی گیت‌هاب می‌فرستد. سرور گیت‌هاب فایل را با سرعت بالا دانلود کرده، آن را قطعه‌بندی می‌کند و در ریپازیتوری شخصی شما قرار می‌دهد. سپس می‌توانید لینک‌های مستقیم فایل را دریافت کنید.

### ویژگی‌های کلیدی

| ویژگی | توضیحات |
| :--- | :--- |
| **خوشه‌بندی چند-نودی** | امکان افزودن چندین اکانت گیت‌هاب (Token). برنامه با استفاده از سیستم توزیع بار (Load Balancing)، وظایف دانلود را بین اکانت‌های مختلف پخش می‌کند تا از لیمیت شدن جلوگیری شود. |
| **موتور خالص گیت‌هاب** | وابستگی ۱۰۰ درصدی به زیرساخت گیت‌هاب. حذف کامل فضاهای ابری شخص ثالث برای دستیابی به بالاترین سطح پایداری و معماری ضد-تحریم. |
| **فایل منیجر یکپارچه** | مشاهده، کپی کردن لینک‌ها و حذف فایل‌های موجود در *تمام* ریپازیتوری‌های متصل شده، به صورت همزمان و در یک محیط کاربری واحد. |
| **اکسترکتور داخلی** | ابزار اختصاصی برای سرهم کردن و استخراج فایل‌های زیپ چندتکه‌ی دانلود شده (`zip.001.`) در سیستم ویندوز شما. |
| **حریم خصوصی و امنیت** | قابلیت تغییر وضعیت ریپازیتوری‌ها (خصوصی/عمومی) از داخل برنامه و امکان رمزنگاری (Hash) نام فایل‌ها پیش از انتقال به سرور. |

### نحوه کارکرد
۱. **ارسال درخواست:** شما لینک دانلود مستقیم را در برنامه وارد می‌کنید.
۲. **اجرای اکشن:** برنامه یک دستور (Workflow Dispatch) به یکی از ریپازیتوری‌های متصل شده ارسال می‌کند.
۳. **پردازش ابری:** سرور ابری گیت‌هاب فایل هدف را دانلود می‌کند.
۴. **قطعه‌بندی:** از آنجایی که گیت‌هاب محدودیت حجم برای هر فایل دارد، سرور به طور خودکار فایل را زیپ کرده و به قطعات ۹۰ مگابایتی تقسیم می‌کند.
۵. **دریافت لینک:** قطعات در پوشه `downloads` ذخیره می‌شوند و اکتافچ لینک‌های خام (Raw) آن‌ها را برای دانلود در اختیار شما می‌گذارد.

### پیش‌نیازها
* سیستم عامل ویندوز
* حساب کاربری GitHub
* یک توکن **PAT (Personal Access Token)** از نوع Fine-grained با دسترسی‌های `repo` و `workflow`.

### راهنمای استفاده
۱. به تب **⚙️ Node Management** بروید.
۲. توکن گیت‌هاب و یک نام دلخواه برای ریپازیتوری (مثلاً `Cloud-Downloads`) وارد کرده و دکمه **Add Node** را بزنید.
۳. روی دکمه **Test All Nodes** کلیک کنید تا اتصال بررسی شده و کدهای لازم به صورت خودکار در ریپازیتوری شما ساخته شوند.
۴. به تب **🚀 Dashboard** بروید، لینک خود را وارد کنید و روی **Start Cloud Transfer** کلیک کنید.

---
*Developed by King Network*