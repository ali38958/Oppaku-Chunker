<div align="center">

# 🤝 Contributing to Oppaku

Thank you for your interest in improving **Oppaku**!

[![Author: Muhammad Ali](https://img.shields.io/badge/Author-Muhammad%20Ali-blue?style=flat-square)](https://github.com/ali38958)
[![License: Personal Use Only](https://img.shields.io/badge/License-Personal%20Use%20Only-orange.svg?style=flat-square)](LICENSE.md)
[![PRs Welcome](https://img.shields.io/badge/PRs-Welcome-brightgreen.svg?style=flat-square)](https://github.com/ali38958/Oppaku-Chunker/pulls)

</div>

---

## 📌 Project Ownership & Vision

**Oppaku** is a personal project created, developed, and maintained by **Muhammad Ali**. 

While the project source code is available for personal use and study under the [Oppaku License](LICENSE.md), community help in squashing bugs, refining cryptographic integrity, optimizing memory efficiency, or proposing useful enhancements is warmly welcomed through **GitHub Pull Requests**.

> [!IMPORTANT]
> To keep the project unified and protect its integrity, all intellectual property, project branding, and final release authority remain exclusively with **Muhammad Ali**. By submitting a Pull Request, you agree to the [Contribution Terms](#-contribution-terms--rights) below.

---

## 🛠️ How You Can Contribute

We welcome contributions in several areas:

- 🐛 **Bug Fixes:** Fixing unexpected crashes, edge-case chunking issues, or sparse-file allocation errors.
- ⚡ **Performance Optimizations:** Enhancing stream throughput, Brotli compression pipelines, or memory footprint.
- 🎨 **UI/UX Polish:** Improving WPF themes, responsive layouts, or accessibility.
- 🧪 **Automated Testing:** Expanding test coverage for cryptographic hashing, archive packing, and sparse reassembly.
- 📖 **Documentation:** Clarifying instructions, fixing typos, or improving code comments.

---

## 🚀 Pull Request Workflow

Follow these steps to submit a fix or feature:

### 1. Check Existing Issues
Before starting work, check the [GitHub Issues](https://github.com/ali38958/Oppaku-Chunker/issues) page to see if your bug or feature is already being discussed. If you plan to make a significant change, please open an issue first to discuss the design.

### 2. Fork & Create a Branch
Fork the repository on GitHub for the purpose of creating your Pull Request:
```bash
git clone https://github.com/<your-username>/Oppaku-Chunker.git
cd Oppaku-Chunker
git checkout -b fix/your-bug-fix-name
# or
git checkout -b feat/your-feature-name
```

### 3. Development Guidelines
- **Target Framework:** .NET 10.0 (`net10.0` and `net10.0-windows`).
- **Zero-Storage Philosophy:** Oppaku is built around zero intermediate temp files and streaming. Any chunking or extraction logic must respect this architecture.
- **Code Style:** Follow clean, modern C# 12 / .NET 10 conventions. Keep code concise, self-documenting, and free of bloat.
- **No Telemetry / Malware:** Pull requests introducing tracking, telemetry, or external network dependencies will be immediately rejected.

### 4. Test Your Changes
Always run the automated test suite before opening a PR:
```bash
dotnet test
```
If you are adding a new feature or fixing a bug, please include corresponding unit tests in the `tests/` directory.

### 5. Submit Your Pull Request
1. Commit your changes with clear, descriptive commit messages:
   ```bash
   git commit -m "fix(rebuilder): resolve edge case in sparse byte allocation"
   ```
2. Push your branch to your fork:
   ```bash
   git push origin fix/your-bug-fix-name
   ```
3. Open a Pull Request against the `main` branch of the official [Oppaku Repository](https://github.com/ali38958/Oppaku-Chunker).
4. Fill out the PR description with:
   - What changed
   - Why the change is necessary
   - How you tested and verified the fix

---

## 🔍 Review & Merge Process

- **Personal Review:** Muhammad Ali will personally review all Pull Requests.
- **Constructive Feedback:** You may be asked to make adjustments, refine tests, or rebase your branch.
- **Merge Decision:** Merging into the `main` branch is at the sole discretion of Muhammad Ali to maintain the project's quality, stability, and vision.

---

## ⚖️ Contribution Terms & Rights

To ensure that the project remains safe, open for personal use, and free of legal ambiguity:

1. **Grant of Rights:** By submitting a Pull Request, patch, or code contribution to this repository, you grant **Muhammad Ali** a perpetual, worldwide, non-exclusive, royalty-free, irrevocable license to use, incorporate, modify, adapt, compile, release, and distribute your contribution as part of Oppaku under the project's license.
2. **Sole Project Ownership:** You acknowledge that submitting contributions does not grant you ownership, copyright, or trademark rights over Oppaku, its binary distributions, or the official repository. Full ownership remains with **Muhammad Ali**.
3. **Original Work Warranty:** You represent that your contribution is your own original work and that you have the right to submit it without violating any third-party licenses, patents, or intellectual property rights.

---

<div align="center">

Thank you for helping make **Oppaku** even better! 🚀

**Muhammad Ali** — [GitHub Profile](https://github.com/ali38958) • [Oppaku Repository](https://github.com/ali38958/Oppaku-Chunker)

</div>
