# StS2-iOS-Launcher

**본인이 Steam에서 산 Slay the Spire 2를 iPhone 네이티브 앱으로 빌드합니다.** 스트리밍이 아니라 게임이 폰에서 직접,
오프라인으로 돌아가고, 세이브는 Steam 클라우드로 PC와 공유됩니다.

[English README](README.md)

> **먼저 읽어 주세요.**
> 이 저장소에는 **Slay the Spire 2의 어떤 부분도 들어 있지 않습니다.** 게임 코드, 에셋, 오디오, 팩 파일이 전혀 없고,
> 직접 작성한 빌드 도구만 있습니다. 이 도구는 **본인이 Steam에서 구매한 게임 사본**을 본인의 Mac에서 가공해 본인의
> iPhone에 설치하는 용도입니다.
> - 비공식 프로젝트입니다. Mega Crit이나 Valve와 무관하며 승인·지원을 받지 않았습니다. 이 프로젝트 관련 문의를
>   그쪽에 하지 마세요.
> - **빌드한 앱이나 .ipa를 절대 공유하지 마세요.** Mega Crit의 게임이 들어 있습니다.
> - 사용에 따른 책임은 본인에게 있습니다([LICENSE](LICENSE), 무보증). 세이브는 백업해 두세요.
> - 권리자께서는 이슈를 열어 주시면 바로 대응하겠습니다.
>
> 법적 구조는 안드로이드판 [Ekyso/StS2-Launcher](https://github.com/Ekyso/StS2-Launcher)를 따릅니다.

## 무엇이 되나

- iPhone에서 게임 전체: OpenGL ES 3(Compatibility) 렌더러, FMOD 오디오, 좌우 가로 방향. 터치는 Godot 기본
  터치→마우스 변환으로 동작합니다(탭 = 클릭, 드래그 = 드래그).
- **Steam 클라우드 세이브 자동 동기화.** PC에서 하던 런을 폰에서 이어 하고, 다시 PC로 돌아갈 수 있습니다. 폰은
  게임이 세이브를 읽기 전에 받아 오고, 앱을 나갈 때 올립니다.
- 4GB iPhone 12에서 동작: 메뉴 약 650MB, 전투 최대 780MB (앱 한도 약 2GB).
- **멀티플레이(선택)**: 본인 PC를 중계로 써서 Steam 친구와 플레이. 별도 서버 없음.

## 동작 원리

iOS는 JIT 컴파일을 금지하므로 안드로이드 방식(실행 중 다운로드 후 Harmony로 패치)은 불가능합니다. 모든 작업을
Mac에서 미리 합니다.

```
Steam 설치본 ─┬─ 게임 어셈블리 ──► cecilpatch(빌드 타임 IL 수정) ──► Godot iOS 익스포트(NativeAOT) ─┐
              └─ 게임 팩(.pck) ──► ASTC 텍스처 + iOS 설정 + 셰이더 수정 ────────────────────────────┴─► 서명된 앱 ─► iPhone
```

- **어셈블리 패치**(`tools/cecilpatch`): iOS엔 Steam 클라이언트가 없으므로 게임의 Steam 플랫폼 초기화를 로컬
  초기화로 바꿉니다. 클라우드 동기화 훅과 "게임 종료 = 앱 종료"도 넣습니다. 실행 중 패치는 없습니다.
- **게임 팩**(`tools/pck/build_pck.py`): 데스크톱 텍스처(S3TC/BPTC)를 iPhone GPU가 읽는 ASTC로 다시 인코딩합니다.
  Forward+ 렌더러는 파이프라인 캐시만으로 메모리 한도를 넘기 때문에 Compatibility(GLES3)로 바꿉니다. 프레임은
  30fps로 낮추고, Apple GPU에서 폴트를 내는 셰이더 하나(배열 범위 밖 읽기)를 고칩니다.
- **네이티브 확장**(`tools/build_natives.sh`): 게임이 쓰는 FMOD와 Spine GDExtension을 공식 소스에서 iOS용으로
  다시 빌드합니다.

자세한 내용과 측정치: [docs/architecture.md](docs/architecture.md), [docs/in-app-sync-design.md](docs/in-app-sync-design.md).

## 준비물

- Apple 실리콘 Mac, **Xcode**(Command Line Tools만으로는 부족), `brew install scons dotnet protobuf`
- **Godot 4.5.1 .NET** 에디터와 익스포트 템플릿 (godotengine.org, "4.5.1-stable mono")
- 그 Mac에 설치된 **Steam판 Slay the Spire 2**
- **iOS용 FMOD Engine**: [fmod.com](https://www.fmod.com/download)에서 가입 후 무료 다운로드. FMOD 라이선스는 본인이
  동의하는 것이며 이 저장소는 재배포하지 않습니다.
- iPhone과 서명용 Apple ID. 무료 Apple ID도 되지만 7일마다 다시 서명해야 합니다. 유료 개발자 계정은 1년입니다.

iPhone 12, iOS 18에서 검증했습니다. 다른 iPhone도 동작할 것으로 보지만 iPad와 더 낮은 iOS는 검증하지 않았습니다.

## 설치

```bash
git clone https://github.com/<you>/StS2-iOS-Launcher && cd StS2-iOS-Launcher
make config        # config.mk 생성: 경로, Apple 팀 ID, 서명 ID, 번들 ID, 기기를 채웁니다
make natives       # 최초 1회: iOS용 FMOD + Spine (약 10분)
make build         # 어셈블리, 텍스처(처음엔 약 10분), 팩, 익스포트(약 10분), 서명
make install       # 폰에 설치 (케이블 또는 같은 Wi-Fi)
```

클라우드 세이브는 최초 1회만:

```bash
make login         # Steam 모바일 앱으로 QR 코드 스캔
make provision     # 그 로그인을 폰 앱으로 복사
```

앱을 처음 열 때 iOS가 개발자 인증서 신뢰를 요구합니다: 설정 > 일반 > VPN 및 기기 관리.

`make help`로 전체 타깃을 볼 수 있습니다. 개인 값은 모두 git에서 제외되는 `config.mk`에만 있습니다.

### 서명 유지

무료 Apple ID는 7일 뒤 서명이 만료돼 앱이 열리지 않습니다. `make resign`(세이브 유지)을 하거나, `make ipa`로 만든
파일을 [AltStore](https://altstore.io)로 설치하세요. AltStore는 폰과 Mac이 같은 네트워크에 있으면 백그라운드에서
자동으로 다시 서명합니다.

### 게임 업데이트

폰은 빌드한 시점의 게임 버전으로 돌아갑니다. Steam이 게임을 업데이트하면 `make build install`을 다시 하세요.
바뀐 텍스처만 다시 인코딩합니다. **동기화하는 모든 기기에서 같은 게임 브랜치(정식 또는 베타)를 쓰세요.** 구버전은
세이브를 읽을 때 모르는 콘텐츠를 버립니다. 동기화는 그런 세이브를 더 새로운 세이브 위에 올리지 않지만, 버전을
맞추는 편이 매끄럽습니다.

## 세이브와 Steam 클라우드

- 앱을 켜면 폰은 **게임이 세이브를 읽기 전에** 프로필, 진행도, 진행 중인 런을 최신으로 맞춥니다. 네트워크가 없으면
  8초 뒤 포기하고 폰의 세이브로 시작합니다.
- 앱을 나가면 바뀐 것을 올립니다.
- 파일마다 양쪽이 마지막으로 합의한 버전과 비교합니다. 한쪽만 바뀌었으면 그쪽이 이깁니다. 양쪽이 다 바뀌었으면
  진행도로 비교하고, 진 쪽 사본은 앱 안의 `save-backups/`에 남깁니다.
- 한 기기에서 끝낸 런은 다른 기기에서도 사라집니다. 런 기록은 게임 자체의 클라우드 한도를 따릅니다: 최근 100개가
  공유되고, 그보다 오래된 기록은 각 기기에 남습니다.
- Steam 로그인은 앱에 저장된 리프레시 토큰입니다. Steam 계정의 인증된 기기 목록(Steam Guard 설정)에서 언제든
  해제할 수 있고, `sts2ios`로 표시됩니다.

폰은 이 게임의 클라우드 파일만 읽고 쓰는 작은 자체 클라이언트(`syncmgr/SteamCloudClient.cs`)로 Steam과
통신합니다. 비공식이므로 Valve가 바꾸거나 막을 수 있고, 서드파티 클라이언트 사용은 Steam 구독 계약상 본인 책임입니다.

평소엔 케이블이 필요 없습니다. `make sync-pull`과 `make sync-push`는 데스크톱 대체 수단으로 남아 있습니다.

## 멀티플레이 (선택)

iPhone에는 Steam 클라이언트가 없습니다. 본인 PC가 본인 게임의 패치된 사본을 브리지로 실행합니다. 친구는 평소처럼
Steam 로비에 들어오고, 폰은 UDP로 브리지에 연결합니다. 프로젝트 서버는 없습니다. `config.mk`에 `MP=1`을 넣고
`make bridge-setup`, `make build install`, 그리고 매번 `make bridge-run`을 하세요.
[docs/architecture.md](docs/architecture.md), [docs/reachability.md](docs/reachability.md)를 참고하세요. 모두 같은 게임
버전이어야 합니다.

## 알려진 한계

- **서명**: 무료 Apple ID는 7일 만료(위 참고). 무료 ID당 사이드로드 앱은 3개까지입니다.
- **그래픽**: Compatibility 렌더러엔 2D MSAA/FXAA와 파티클 서브이미터가 없습니다. 어떤 효과가 처음 나올 때 셰이더를
  컴파일하느라 잠깐 끊길 수 있고, 이후엔 캐시됩니다.
- **발열**: 긴 전투에선 폰이 따뜻해집니다(4분 이상 시 "Fair"). 기본 30fps이며 게임 내 프레임 제한 설정도 동작합니다.
- **터치**: 기본 터치→마우스 변환이라 우클릭이 없고, 화면은 16:9에 좌우 여백이 있습니다.
- 폰에서는 도전 과제와 Steam 오버레이 기능을 쓸 수 없습니다.

## 문제 해결

| 증상 | 해결 |
|---|---|
| 앱이 안 열림, "신뢰하지 않는 개발자" | 설정 > 일반 > VPN 및 기기 관리 > 신뢰 |
| 일주일 뒤 앱이 안 열림 | `make resign` (무료 Apple ID) |
| 세이브가 동기화되지 않음 | `make logs`로 앱의 `sync_log.txt`를 Mac으로 가져와 확인. 로그인이 거부됐다고 나오면 `make provision` 다시 |
| 익스포트 후 검은 화면 | 기기에선 `--export-release` 빌드만 동작합니다. 에디터 대신 `make export`를 쓰세요 |
| `make natives`가 SDK를 못 찾음 | Xcode를 설치하세요. Command Line Tools에는 iOS SDK가 없습니다 |

## 저장소 구조

| 경로 | 내용 |
|---|---|
| `Makefile`, `config.mk.example` | 전체 작업 흐름 |
| `tools/cecilpatch/` | 빌드 타임 어셈블리 패처 (Mono.Cecil) |
| `tools/pck/` | iOS 게임 팩 빌더와 ASTC 트랜스코더 |
| `tools/build_natives.sh`, `patches/` | FMOD와 Spine iOS 빌드 |
| `tools/assemble.sh` | 앱 번들 조립과 서명 |
| `syncmgr/` | 앱 내 동기화와 iOS 생명주기; `syncmgr/tests/` 오프라인 테스트(`make test`) |
| `tools/stssync/` | 데스크톱 Steam 로그인과 세이브 동기화 |
| `bridge/` | 멀티플레이 PC 브리지 |
| `gameapp/` | 익스포트용 Godot 프로젝트 (게임 코드 없음; 어셈블리는 `make libs`가 복사) |
| `docs/` | 설계 노트, 측정, [ROADMAP](docs/ROADMAP.md) |

## 크레딧

[Ekyso/StS2-Launcher](https://github.com/Ekyso/StS2-Launcher)(안드로이드 런처; 법적 구조와 패치 참고),
[SocialHummingbird/StS2-Launcher-Overhaul](https://github.com/SocialHummingbird/StS2-Launcher-Overhaul)(Steam 클라우드
참고), [Godot](https://godotengine.org), [FMOD](https://www.fmod.com), [Spine](https://esotericsoftware.com),
[fmod-gdextension](https://github.com/utopia-rise/fmod-gdextension),
[SteamDatabase/Protobufs](https://github.com/SteamDatabase/Protobufs). 서드파티 라이선스:
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## 라이선스

이 저장소의 코드는 MIT([LICENSE](LICENSE)). Slay the Spire 2는 Mega Crit의 소유입니다.
