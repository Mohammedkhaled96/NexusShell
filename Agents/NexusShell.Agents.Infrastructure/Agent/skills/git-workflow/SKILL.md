---
name: git-workflow
description: Safe everyday git workflows - branching, syncing, undoing mistakes, resolving conflicts and rewriting history without losing work.
---
# Git workflow

## Inspect first
- `git status -sb` - branch, upstream and changed files.
- `git log --oneline --graph -15` - recent history.
- `git branch -vv` - local branches and what they track.

## Everyday flow
1. Create a branch from an up-to-date base: `git switch main` then `git pull --ff-only` then `git switch -c feature/name`.
2. Commit small, focused changes: `git add -p` lets the user review each hunk.
3. Publish: `git push -u origin feature/name`.

## Undo safely
| Situation | Command | Rewrites history? |
|---|---|---|
| Unstage a file | `git restore --staged <file>` | No |
| Discard local edits to a file | `git restore <file>` | No (but edits are lost) |
| Fix the last commit message (not pushed) | `git commit --amend` | Yes |
| Undo a pushed commit | `git revert <sha>` | No |
| Go back locally, keep changes | `git reset --soft HEAD~1` | Yes |

Before any history rewrite, create a safety branch: `git branch backup/before-fix`.
Recover "lost" commits with `git reflog`.

## Pushing rewritten history
Use `git push --force-with-lease`, never plain `--force`: it refuses to overwrite commits someone else pushed.

## Conflicts
1. `git status` lists conflicted files.
2. Edit each file to resolve the conflict markers, then `git add <file>`.
3. Continue with `git merge --continue` or `git rebase --continue`; abort with `--abort` to return to the previous state.
