# StS2 Undo

**Slay the Spire 2** 전투 중 카드 플레이와 턴을 되돌릴 수 있게 해주는 모드입니다.

> 카드를 잘못 냈거나, 파워 사용 순서를 헷갈렸나요? **Z** 키만 누르세요.

![에너지 표시 옆의 전투 중 되돌리기 버튼](docs/screenshots/preview-undo.png)

[English README](README.md)

**Nexus Mods:** https://www.nexusmods.com/slaythespire2/mods/716

---

## 기능

- `Z` 키로 **카드 한 장 단위 되돌리기**
- `Shift+Z` 키로 **턴 단위 되돌리기** — 현재 플레이어 턴 시작 시점으로 복귀
- 에너지 표시 옆에 **전투 중 되돌리기 버튼** 표시 (`Z` 단축키와 동일하게 작동)
- 활성 전투 중에만 동작 — *사망 후에는 되돌릴 수 없고, 맵 화면에서는 사용할 수 없습니다*
- 전투당 최대 **30개**의 스냅샷 유지
- **싱글플레이 전용** — 매니페스트에 `"affects_gameplay": true`로 표기되어 있어,
  멀티플레이 세션에서는 게임의 모드 로더가 본 모드를 비활성화합니다.
  협동 플레이에서는 사용할 수 없으며, 강제로 사용하면 다른 플레이어와의 상태가
  어긋날 수 있으므로 시도하지 마세요.

## 작동 방식

다음 액션이 시작되는 시점에 **전투 상태의 deep snapshot**을 만듭니다:

- 카드 사용 (`PlayCardAction`)
- 턴 종료 (`EndPlayerTurnAction`)
- 포션 사용 / 버리기 (`UsePotionAction`, `DiscardPotionGameAction`)

`Z`를 누르면 가장 최근 스냅샷에서 HP, 방어도, 에너지, 손패/드로/버림/소멸 더미, 파워, 유물, 포션, 오브, 몬스터 인텐트, RNG, 비주얼 상태(spine 애니메이션, modulate, 히트박스 등)를 모두 복원합니다.

**스냅샷 방식**을 채택했습니다 — 카드/상태이상 상호작용이 너무 많아 inverse 연산을 일일이 정의하기 어렵기 때문에, 캡처-앤-복원 전략을 사용합니다.

## 단축키

| 키        | 동작                                   |
|-----------|----------------------------------------|
| `Z`       | 한 스텝 되돌리기 (최근 카드/포션/턴종료) |
| `Shift+Z` | 현재 턴의 시작 시점으로 되돌리기       |

에너지 표시 옆에 나타나는 `↶ Z` 버튼을 클릭해도 됩니다.
버튼을 **우클릭한 채로 드래그**하면 원하는 위치로 옮길 수 있고, 저장된
위치는 `%APPDATA%\Sts2UndoMod\settings.json`에 기록되어 다음 전투부터
적용됩니다. 위치를 초기화하려면 해당 파일을 삭제하세요.

## 설치

1. [Nexus Mods](https://www.nexusmods.com/slaythespire2/mods/716) 또는
   [GitHub Releases](../../releases)에서 `Sts2UndoMod.dll`과 `Sts2UndoMod.json`을 받습니다.
2. 두 파일을 다음 경로에 복사합니다:
   ```
   <Slay the Spire 2 설치 폴더>/mods/Sts2UndoMod/
   ```
3. 게임을 실행합니다.

> **안정판 vs 베타 브랜치:** 버전마다 두 가지 빌드가 배포됩니다 —
> **안정판** 브랜치용 `Sts2UndoMod-vX.Y.Z.zip` (STS2 v0.103.x) 과
> **베타 옵트인** 브랜치용 `Sts2UndoMod-vX.Y.Z-beta.zip` (STS2 v0.104.0+,
> 현재 v0.106.x). 본인 브랜치에 맞는 파일을 받으세요 (Steam → Slay the
> Spire 2 → 속성 → 베타). 브랜치 간 STS2 API가 다르기 때문에, 잘못된 빌드를
> 쓰면 첫 카드 사용 시 `MissingMethodException`이 발생합니다.

## 소스에서 빌드

필요한 환경:
- .NET SDK 9.0
- Godot.NET.Sdk 4.5.1 (`Directory.Build.props`가 자동으로 처리)
- 로컬에 설치된 Slay the Spire 2 (`Sts2PathDiscovery.props`가 Steam 레지스트리/표준 경로에서 자동 탐색)

```sh
dotnet build Sts2UndoMod.csproj -c Release
```

빌드가 끝나면 `Sts2UndoMod.dll`과 `Sts2UndoMod.json`이 `<sts2>/mods/Sts2UndoMod/`에 자동 복사됩니다 (`CopyToModsFolderOnBuild` 타겟).

## 로그

모드는 다음 경로에 로그를 남깁니다:
```
%APPDATA%/Sts2UndoMod/probe.log   (Windows)
~/.config/Sts2UndoMod/probe.log   (Linux/macOS)
```
이슈 등록 시 함께 첨부해 주세요.

## 변경 이력

전체 내용은 [GitHub Releases](../../releases) 참조.

- **v0.0.12** — 카드 한 장을 냈는데 `Z`를 두 번 눌러야 되돌려지던 버그 수정
  (스냅샷 중복 방지 게이트가 존재하지 않는 `ActionExecutor` 필드를 검사해서
  수동 플레이마다 스냅샷이 두 번 찍히던 문제). 유물이 많은 덱에서 카드 사용
  시마다 생기던 끊김도 수정 (캡처 핫패스의 중복 유물 deep-clone 제거).
- **v0.0.10** — 멀티플레이 자동 비활성화, 인스턴스 파워(오빗) 되돌리기 정확도,
  자매 모드([Sts2CombatAI](https://github.com/ing-gom/sts2-combat-ai)) AI
  자동 플레이 커버리지.

## 크레딧

이 모드는 앞서 만들어진 작업들 위에 서 있습니다. 다음 분들께 감사드립니다:

- **JiesiLuo** — Slay the Spire 2용
  [`UndoAndRedo`](https://github.com/luojiesi/SLS2Mods) 모드 제작자.
  본 모드의 직접적인 베이스가 된 작업으로, 전투 상태 스냅샷 아키텍처와
  핵심 기반 작업의 다수가 해당 프로젝트에서 비롯되었습니다.
- **filippobaroni** — Slay the Spire 1용
  [`Undo the Spire`](https://github.com/filippobaroni/undo-the-spire) 제작자.
  전투 전체 스냅샷 기반 Undo 라는 핵심 컨셉의 원조입니다.
- **MegaCrit** — Slay the Spire 2 개발사.
- **HarmonyX** — 본 모드가 사용하는 런타임 패칭 라이브러리 (게임에 동봉되어
  있으며, 본 모드는 별도 재배포하지 않습니다).

## 라이선스

[MIT](LICENSE).
