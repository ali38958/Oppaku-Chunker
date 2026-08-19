# Repository Cleanup & Security Plan

## Overview
The goal is to secure the `.agents` directory so proprietary agent configurations are not leaked when pushing to GitHub, and to ensure the repository is completely clean of unnecessary artifacts.

## Project Type
BACKEND / TOOLING

## Success Criteria
- `.agents` directory is completely excluded from Git tracking.
- Any previously tracked `.agents` files are removed from Git history/cache.
- The repository contains only source code, tests, and documentation.

## Tech Stack
- **Git**: `.gitignore` configuration and cache clearing (`git rm -r --cached`).

## File Structure
```text
.gitignore (To be modified)
```

## Task Breakdown

### Task 1: Update `.gitignore`
- **Agent:** `devops-engineer`
- **Skills:** `bash-linux`, `powershell-windows`
- **INPUT:** Current `.gitignore`
- **OUTPUT:** Modified `.gitignore` containing `.agents/`
- **VERIFY:** `git status` shows `.agents/` is ignored.

### Task 2: Purge Git Cache
- **Agent:** `devops-engineer`
- **Skills:** `bash-linux`, `powershell-windows`
- **INPUT:** Git index containing `.agents` files (if already tracked)
- **OUTPUT:** Clean Git index without `.agents`
- **VERIFY:** `git ls-files | grep .agents` returns nothing.

### Task 3: Final Repository Audit
- **Agent:** `security-auditor`
- **Skills:** `lint-and-validate`
- **INPUT:** Entire repository
- **OUTPUT:** Final verification report
- **VERIFY:** No binary files or proprietary agent files are staged for commit.

## Phase X: Verification
- [ ] Security Scan (Ensure no secrets in remaining files)
- [ ] Build Check (Ensure project still builds without ignored files)
- [ ] Git Status Check (Verify clean working tree ready for push)
