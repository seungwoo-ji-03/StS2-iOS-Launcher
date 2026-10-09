# StS2-iOS-Launcher: build Slay the Spire 2 for your own iPhone from your own Steam copy.
#
#   make config        once: create config.mk, then fill it in
#   make natives       once: FMOD + spine iOS libraries (needs the FMOD iOS SDK, see README)
#   make login         once: Steam login for save sync (QR code)
#   make build         everything below the line, in order
#   make install       install on the phone (keeps saves), then `make provision` the first time
#   ---------------------------------------------------------------------------------------------
#   make libs          game assemblies from the Steam install -> gameapp/libs
#   make syncmgr       in-app Steam Cloud sync module -> gameapp/libs
#   make patch         iOS patches into the game assembly (Steam platform init, sync hook, quit, fps)
#   make textures      desktop textures -> ASTC (slow the first time, resumable, redone after game updates)
#   make pck           iOS game pack (textures, native-extension descriptors, settings, shader fixes)
#   make export        Godot iOS export (NativeAOT)
#   make app           assemble + sign the installable app
# Works with the GNU Make 3.81 that ships with macOS: every multi-step recipe is a single shell command
# (3.81 has no .ONESHELL), started with `set -e`.
SHELL := /bin/bash
.SILENT:
.DEFAULT_GOAL := help
export DEVELOPER_DIR := /Applications/Xcode.app/Contents/Developer
export DOTNET_CLI_TELEMETRY_OPTOUT := 1
export DOTNET_NOLOGO := 1

-include config.mk

REPO      := $(CURDIR)
GAMEAPP   := $(REPO)/gameapp
LIBS      := $(GAMEAPP)/libs
GAME_RES   = $(GAME_APP)/Contents/Resources
GAME_DATA  = $(GAME_RES)/data_sts2_macos_arm64
GAME_PCK   = $(GAME_RES)/Slay the Spire 2.pck
TC        := $(WORKDIR)/textures
PCK_OUT   := $(WORKDIR)/sts2_ios.pck
APP       := $(WORKDIR)/sts2.app
EXPORTED   = $(GAMEAPP)/build/sts2.xcarchive/Products/Applications/sts2.app
CECIL     := dotnet run -c Release --project $(REPO)/tools/cecilpatch --
STAMP     := $(shell date +%Y%m%d-%H%M%S)
# your own Steam display name for multiplayer: MP_NAME, else read from Steam's loginusers.vdf for MP_NETID
MP_NAME_RESOLVED = $(or $(MP_NAME),$(shell python3 "$(REPO)/tools/steam_persona.py" "$(MP_NETID)" 2>/dev/null))
# game version stamp (release_info.json: version + main assembly hash); every stage records the one it used
GAME_VERSION = $(shell python3 -c 'import json,sys;d=json.load(open(sys.argv[1]));print(d["version"]+"-"+str(d["main_assembly_hash"]))' "$(GAME_RES)/release_info.json" 2>/dev/null)
PATCH_FLAGS = --sync --quit-exit --fps-default=30 $(if $(filter 1,$(MP)),--mp --mp-config --mp-join=$(MP_JOIN) --mp-netid=$(MP_NETID) $(if $(MP_NAME_RESOLVED),--mp-name="$(MP_NAME_RESOLVED)"))

.PHONY: help config guard build libs syncmgr patch natives textures pck export app install deploy provision \
        login logs resign test sync-pull sync-push ipa bridge-setup bridge-run measure clean

help: ## list targets
	@grep -hE '^[a-z-]+:.*##' $(MAKEFILE_LIST) | awk 'BEGIN{FS=":.*##"}{printf "  \033[36m%-12s\033[0m %s\n",$$1,$$2}'

config: ## create config.mk from the template
	@[ -f config.mk ] && echo "config.mk exists" || { cp config.mk.example config.mk; echo "created config.mk: fill it in"; }

guard:
	@[ -f config.mk ] || { echo "no config.mk: run make config"; exit 1; }
	@[ -d "$(GAME_APP)" ] || { echo "GAME_APP not found: $(GAME_APP)"; exit 1; }
	@mkdir -p "$(WORKDIR)"

build: libs syncmgr patch textures pck export app ## full build (run `make natives` once first)

libs: guard ## copy the game's managed assemblies from your Steam install
	set -e; mkdir -p "$(LIBS)"; \
	find "$(GAME_DATA)" -maxdepth 1 -name '*.dll' ! -name 'bridgemgr.dll' -exec cp -p {} "$(LIBS)/" \; ; \
	if [ -f "$(GAME_DATA)/sts2.dll.orig" ]; then cp -p "$(GAME_DATA)/sts2.dll.orig" "$(LIBS)/sts2.dll.orig"; \
	else cp -p "$(GAME_DATA)/sts2.dll" "$(LIBS)/sts2.dll.orig"; fi; \
	echo "$(GAME_VERSION)" > "$(WORKDIR)/libs.version"; \
	echo "libs: $$(ls "$(LIBS)"/*.dll | wc -l | tr -d ' ') assemblies from game $(GAME_VERSION)"

# DebugType=none: no path to an external .pdb is recorded. The NativeAOT compiler follows such a path, and a
# .pdb from a later rebuild makes it fail with "Invalid compressed integer".
syncmgr: guard ## build the in-app sync module into gameapp/libs
	set -e; dotnet build "$(REPO)/syncmgr/syncmgr.csproj" -c Release --no-incremental -p:DebugType=none -p:DebugSymbols=false -o "$(WORKDIR)/syncmgr-out" >/dev/null; \
	cp "$(WORKDIR)/syncmgr-out/syncmgr.dll" "$(WORKDIR)/syncmgr-out/Google.Protobuf.dll" "$(LIBS)/"; \
	echo "syncmgr: $$(stat -f %z "$(LIBS)/syncmgr.dll") bytes"

patch: guard ## apply the iOS patches to the game assembly
	[ -f "$(LIBS)/sts2.dll.orig" ] || { echo "run make libs first"; exit 1; }
	$(CECIL) --game "$(LIBS)/sts2.dll.orig" "$(LIBS)/sts2.dll" $(PATCH_FLAGS)

natives: guard ## build FMOD + spine iOS libraries (FMOD_IOS_SDK in config.mk)
	"$(REPO)/tools/build_natives.sh" "$(FMOD_IOS_SDK)" "$(WORKDIR)/natives"

textures: guard ## transcode the game's textures to ASTC (resumable)
	set -e; python3 "$(REPO)/tools/pck/build_pck.py" extract "$(GAME_PCK)" "$(TC)"; \
	"$(GODOT)" --headless --path "$(TC)" -s res://transcode.gd | grep -E 'DONE|ERROR' || true

pck: guard ## build the iOS game pack
	set -e; python3 "$(REPO)/tools/pck/build_pck.py" build "$(GAME_PCK)" "$(TC)" "$(PCK_OUT)"; \
	echo "$(GAME_VERSION)" > "$(WORKDIR)/pck.version"

# Personal team/bundle IDs are written into the preset only for the export and always restored (trap), so
# they never end up in a commit. Godot reports "Exported" even when the .NET build failed, so success is
# checked on the produced game framework instead.
export: guard ## Godot iOS export (NativeAOT, ~10 min)
	set -e; \
	[ -f "$(LIBS)/sts2.dll" ] && [ -f "$(LIBS)/syncmgr.dll" ] || { echo "run make libs syncmgr patch first"; exit 1; }; \
	[ -d "$(GAMEAPP)/addons/fmod/libGodotFmod.ios.template_release.xcframework" ] || { echo "run make natives first"; exit 1; }; \
	trap 'git -C "$(REPO)" checkout -- gameapp/export_presets.cfg' EXIT; \
	sed -i '' -E 's|^application/app_store_team_id=.*|application/app_store_team_id="$(TEAM)"|; s|^application/bundle_identifier=.*|application/bundle_identifier="$(BUNDLE)"|' "$(GAMEAPP)/export_presets.cfg"; \
	rm -rf "$(GAMEAPP)/build"; mkdir -p "$(GAMEAPP)/build"; \
	"$(GODOT)" --headless --path "$(GAMEAPP)" --import >/dev/null 2>&1 || true; \
	"$(GODOT)" --headless --path "$(GAMEAPP)" --export-release "iOS" "$(GAMEAPP)/build/sts2.xcodeproj" >"$(WORKDIR)/export.log" 2>&1 || true; \
	if grep -q "Failed to build project" "$(WORKDIR)/export.log" || [ ! -f "$(EXPORTED)/Frameworks/sts2.framework/sts2" ]; then \
	  echo "export failed. Last lines of $(WORKDIR)/export.log:"; grep -E "ERROR: Export|error" "$(WORKDIR)/export.log" | tail -5; \
	  echo "(.NET build log: ~/Library/Application Support/Godot/mono/build_logs/)"; exit 1; fi; \
	cp "$(WORKDIR)/libs.version" "$(WORKDIR)/export.version"; \
	echo "exported: $$(du -h "$(EXPORTED)/Frameworks/sts2.framework/sts2" | cut -f1) game framework"

# game code (export) and game data (pck) must come from the game version currently installed by Steam
app: guard ## assemble and sign the installable app
	set -e; for stage in export pck; do \
	  v="$$(cat "$(WORKDIR)/$$stage.version" 2>/dev/null || echo none)"; \
	  [ "$$v" = "$(GAME_VERSION)" ] || { echo "$$stage was built from game $$v, Steam has $(GAME_VERSION): run make build"; exit 1; }; \
	done; \
	SIGN_ID="$(SIGN_ID)" TEAM="$(TEAM)" BUNDLE="$(BUNDLE)" ICON="$(ICON)" \
	  "$(REPO)/tools/assemble.sh" "$(EXPORTED)" "$(PCK_OUT)" "$(GAME_RES)" "$(APP)"

install: guard ## install on the phone over the existing app (saves are kept)
	set -e; xcrun devicectl device process terminate --device "$(DEV)" --bundle-identifier "$(BUNDLE)" >/dev/null 2>&1 || true; \
	xcrun devicectl device install app --device "$(DEV)" "$(APP)" | grep -iE "installed|error"

deploy: app install ## assemble + install + launch
	xcrun devicectl device process launch --device "$(DEV)" "$(BUNDLE)" >/dev/null && echo "launched"

provision: guard ## copy the Steam login (and MP config if MP=1) into the installed app
	DEV="$(DEV)" BUNDLE="$(BUNDLE)" "$(REPO)/tools/provision_sync.sh" $(if $(filter 1,$(MP)),$(MP_JOIN) $(MP_NETID))

login: ## one-time Steam login by QR code (for save sync)
	cd "$(REPO)/tools/stssync" && dotnet run -c Release -- login

resign: guard ## free Apple ID: renew the 7-day profile and re-sign in place (saves are kept)
	set -e; rm -rf "$(WORKDIR)/resign-tmp"; \
	xcodebuild -project "$(GAMEAPP)/build/sts2.xcodeproj" -scheme sts2 -sdk iphoneos -destination "id=$(DEV)" \
	  -allowProvisioningUpdates -allowProvisioningDeviceRegistration DEVELOPMENT_TEAM="$(TEAM)" CODE_SIGN_STYLE=Automatic \
	  CONFIGURATION_BUILD_DIR="$(WORKDIR)/resign-tmp" build >"$(WORKDIR)/resign.log" 2>&1 || { tail -5 "$(WORKDIR)/resign.log"; exit 1; }; \
	cp "$$(find "$(WORKDIR)/resign-tmp" -name embedded.mobileprovision | head -1)" "$(EXPORTED)/embedded.mobileprovision"; \
	$(MAKE) --no-print-directory app install; \
	echo "re-signed; profile expires $$(security cms -D -i "$(APP)/embedded.mobileprovision" | plutil -extract ExpirationDate raw -o - -)"

logs: guard ## copy the app's sync and game logs from the phone to WORKDIR/logs
	set -e; rm -rf "$(WORKDIR)/logs"; mkdir -p "$(WORKDIR)/logs"; \
	for f in sync_log.txt sync_status.txt logs; do \
	  xcrun devicectl device copy from --device "$(DEV)" --domain-type appDataContainer --domain-identifier "$(BUNDLE)" --source "Documents/$$f" --destination "$(WORKDIR)/logs" >/dev/null 2>&1 || true; \
	done; ls "$(WORKDIR)/logs"; tail -5 "$(WORKDIR)/logs/sync_log.txt" 2>/dev/null || true

test: ## offline sync scenario tests (no Steam account involved)
	cd "$(REPO)/syncmgr/tests" && dotnet run -c Release

sync-pull: guard ## desktop fallback: Steam Cloud -> phone saves (app is closed first)
	set -e; bk="$(WORKDIR)/save-backups/$(STAMP)"; st="$(WORKDIR)/cloudsave"; mkdir -p "$$bk"; rm -rf "$$st"; mkdir -p "$$st"; \
	xcrun devicectl device process terminate --device "$(DEV)" --bundle-identifier "$(BUNDLE)" >/dev/null 2>&1 || true; sleep 2; \
	xcrun devicectl device copy from --device "$(DEV)" --domain-type appDataContainer --domain-identifier "$(BUNDLE)" --source Documents/default/1 --destination "$$bk" >/dev/null 2>&1 || true; \
	(cd "$(REPO)/tools/stssync" && dotnet run -c Release -- pull --dir "$$st"); \
	xcrun devicectl device copy to --device "$(DEV)" --domain-type appDataContainer --domain-identifier "$(BUNDLE)" --source "$$st" --destination Documents/default/1 >/dev/null; \
	echo "pulled (phone copy backed up to $$bk)"

sync-push: guard ## desktop fallback: phone saves -> Steam Cloud
	set -e; bk="$(WORKDIR)/save-backups/$(STAMP)"; mkdir -p "$$bk"; \
	xcrun devicectl device process terminate --device "$(DEV)" --bundle-identifier "$(BUNDLE)" >/dev/null 2>&1 || true; sleep 2; \
	xcrun devicectl device copy from --device "$(DEV)" --domain-type appDataContainer --domain-identifier "$(BUNDLE)" --source Documents/default/1 --destination "$$bk" >/dev/null; \
	cd "$(REPO)/tools/stssync" && dotnet run -c Release -- push --dir "$$bk"

ipa: guard ## package as .ipa for AltStore / SideStore (contains the game: never share it)
	set -e; rm -rf "$(WORKDIR)/ipa"; mkdir -p "$(WORKDIR)/ipa/Payload"; cp -Rc "$(APP)" "$(WORKDIR)/ipa/Payload/"; \
	cd "$(WORKDIR)/ipa"; rm -f "$(WORKDIR)/sts2.ipa"; zip -qry "$(WORKDIR)/sts2.ipa" Payload; \
	echo "wrote $(WORKDIR)/sts2.ipa (for your own device only)"

bridge-setup: guard ## multiplayer PC bridge: build it and inject it into your desktop game
	set -e; GameLibs="$(GAME_DATA)" dotnet build "$(REPO)/bridge" -c Release -o "$(WORKDIR)/bridge-out"; \
	cp "$(WORKDIR)/bridge-out/bridgemgr.dll" "$(GAME_DATA)/bridgemgr.dll"; \
	[ -f "$(GAME_DATA)/sts2.dll.orig" ] || cp "$(GAME_DATA)/sts2.dll" "$(GAME_DATA)/sts2.dll.orig"; \
	$(CECIL) --inject-bridge "$(GAME_DATA)/sts2.dll.orig" "$(GAME_DATA)/sts2.dll"; \
	python3 "$(REPO)/tools/register_bridgemgr_deps.py" "$(GAME_DATA)/sts2.deps.json"; \
	echo "bridge injected: make bridge-run (Steam's 'verify integrity of game files' undoes it)"

bridge-run: guard ## launch the desktop game as the multiplayer bridge
	set -e; cd "$(GAME_APP)/Contents/MacOS" && SteamAppId=2868840 SteamGameId=2868840 nohup "./Slay the Spire 2" --bridge >"$(WORKDIR)/bridge.log" 2>&1 & \
	echo "bridge running (log: $(WORKDIR)/bridge.log). Your friend hosts a lobby; on the iPhone: Multiplayer > Join"

measure: guard ## sample the app's memory and thermal state while you play (ITERS=34)
	set -e; mkdir -p "$(WORKDIR)/traces"; max=0; \
	for i in $$(seq 1 $${ITERS:-34}); do \
	  t="$(WORKDIR)/traces/poll_$$i.trace"; rm -rf "$$t"; \
	  xcrun xctrace record --device "$(DEV)" --template "Activity Monitor" --attach sts2 --time-limit 3s --no-prompt --output "$$t" >/dev/null 2>&1 || true; \
	  mib=$$(xcrun xctrace export --input "$$t" --xpath '/trace-toc/run[@number="1"]/data/table[@schema="sysmon-process"]' 2>/dev/null | python3 "$(REPO)/tools/measure/fp.py"); \
	  th=$$(xcrun xctrace export --input "$$t" --xpath '/trace-toc/run[@number="1"]/data/table[@schema="device-thermal-state-intervals"]' 2>/dev/null | grep -oE 'fmt="(Nominal|Fair|Serious|Critical)"' | tail -1 | sed -E 's/fmt="([^"]*)"/\1/' || true); \
	  echo "[$$i] memory=$$mib MiB thermal=$${th:-?}"; rm -rf "$$t"; \
	  [ "$$mib" != NA ] && awk "BEGIN{exit !($$mib > $$max)}" && max=$$mib || true; \
	done; echo "peak $$max MiB"

clean: ## remove build outputs (keeps transcoded textures and native builds)
	rm -rf "$(GAMEAPP)/build" "$(GAMEAPP)/.godot" "$(WORKDIR)/resign-tmp" "$(WORKDIR)/traces" "$(WORKDIR)/syncmgr-out" "$(WORKDIR)"/*.log
