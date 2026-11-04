#!/usr/bin/env bash
set -euo pipefail

REPO="uWaazy/Local-Achievements"

create() {
  gh label create "$1" --repo "$REPO" --color "$2" --description "$3" --force
}

create "bug" "d73a4a" "Something is not working"
create "enhancement" "a2eeef" "New feature or improvement"
create "feature" "a2eeef" "New feature"
create "fix" "d73a4a" "Bug fix"
create "security" "b60205" "Security related"
create "documentation" "0075ca" "Improvements or additions to documentation"
create "maintenance" "cfd3d7" "Refactoring, build and housekeeping"
create "needs-triage" "fbca04" "Waiting to be reviewed"
create "needs-info" "d876e3" "More information is needed from the reporter"
create "steam-tools" "1d76db" "Steam Tools / Lua Tools games"
create "successstory" "5319e7" "SuccessStory integration"
create "playtime" "0e8a16" "Playtime sync"
create "unsupported-source" "e99695" "Achievement source not supported yet (cracks, emulators)"
create "question" "d876e3" "Further information is requested"
create "good first issue" "7057ff" "Good for newcomers"
create "help wanted" "008672" "Extra attention is needed"
create "ignore-for-release" "ededed" "Hidden from the automatic release notes"
create "duplicate" "cfd3d7" "This issue or pull request already exists"
create "invalid" "e4e669" "This does not seem right"
create "wontfix" "ffffff" "This will not be worked on"
