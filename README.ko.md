# StS2-iOS-Launcher

**Steam에서 산 Slay the Spire 2를 iPhone 네이티브 앱으로 빌드합니다.** 게임은 폰에서 오프라인으로 돌아가고,
세이브는 Steam 클라우드로 PC와 공유됩니다.

[English README](README.md)

<p align="center">
  <a href="docs/media/gameplay.mp4"><img src="docs/media/combat.webp" width="720" alt="iPhone 12에서 돌아가는 Slay the Spire 2: 첫 전투"></a>
</p>
<p align="center"><sub>iPhone 12 실기기 화면을 iPhone 미러링으로 녹화했습니다.
<a href="docs/media/gameplay.mp4">전체 영상(50초)</a>: 앱 실행, 캐릭터 선택, 지도, 첫 전투.</sub></p>

| | |
|:---:|:---:|
| <img src="docs/media/menu.jpg" alt="메인 메뉴"> | <img src="docs/media/map.jpg" alt="1막 지도"> |
| 메인 메뉴 | 지도 |
| <img src="docs/media/combat.jpg" alt="전투"> | <img src="docs/media/reward.jpg" alt="카드 보상"> |
| 전투 | 카드 보상 |

> [!IMPORTANT]
> 이 저장소에는 **게임 파일이 없고**(플레이 스크린샷 제외) 빌드 도구만 있습니다. **본인이 Steam에서 구매한 게임**으로 빌드합니다.
> 비공식 프로젝트이며 Mega Crit, Valve와 무관합니다. **빌드한 앱은 절대 공유하지 마세요.** 사용에 따른 책임은
> 본인에게 있습니다. 권리자 문의는 이슈로 남겨 주세요.

## 기능

- iPhone에서 게임 전체 플레이 (iPhone 12, iOS 18에서 확인)
- Steam 클라우드 세이브 자동 동기화: PC에서 하던 판을 폰에서, 폰에서 하던 판을 PC에서 이어서
- (선택) 내 PC를 중계 삼아 Steam 친구와 멀티플레이

## 준비물

- Apple 실리콘 Mac, Xcode, `brew install scons dotnet protobuf`
- Godot 4.5.1 .NET과 익스포트 템플릿
- Steam으로 설치한 Slay the Spire 2
- [FMOD Engine for iOS](https://www.fmod.com/download) (무료, 가입 필요)
- Apple ID (무료 계정도 되지만 7일마다 다시 서명해야 함)

## 설치

```bash
make config      # config.mk 작성
make natives     # 최초 1회, 약 10분
make build       # 처음엔 약 20분
make install
make login       # Steam 클라우드: Steam 앱으로 QR 스캔
make provision
```

처음 실행할 때 설정 > 일반 > VPN 및 기기 관리에서 개발자를 신뢰해 주세요.
무료 Apple ID라면 7일마다 `make resign`을 실행하세요.

## 한계

- 터치는 탭 = 클릭, 드래그 = 드래그입니다. 우클릭이 없고, 화면은 16:9에 좌우 여백이 있습니다.
- 긴 전투에서는 폰이 따뜻해집니다. 기본 30fps입니다.
- 폰에서는 도전 과제와 Steam 오버레이를 쓸 수 없습니다.

## 문서

[빌드와 문제 해결](docs/build-and-sideload.md) · [세이브 동기화](docs/in-app-sync-design.md) ·
[멀티플레이](docs/architecture.md) · [로드맵](docs/ROADMAP.md)

## 라이선스

MIT ([LICENSE](LICENSE)), 서드파티 라이선스는 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
Slay the Spire 2의 권리는 Mega Crit에 있습니다. [Ekyso/StS2-Launcher](https://github.com/Ekyso/StS2-Launcher),
[StS2-Launcher-Overhaul](https://github.com/SocialHummingbird/StS2-Launcher-Overhaul)에 감사드립니다.
