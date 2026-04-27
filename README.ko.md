# StS2 Undo

**Slay the Spire 2** 전투 중 카드 플레이와 턴을 되돌릴 수 있게 해주는 모드입니다.

> 카드를 잘못 냈거나, 파워 사용 순서를 헷갈렸나요? **Z** 키만 누르세요.

[English README](README.md)

---

## 기능

- `Z` 키로 **카드 한 장 단위 되돌리기**
- `Shift+Z` 키로 **턴 단위 되돌리기** — 현재 플레이어 턴 시작 시점으로 복귀
- 에너지 표시 옆에 **전투 중 되돌리기 버튼** 표시 (`Z` 단축키와 동일하게 작동)
- 활성 전투 중에만 동작 — *사망 후에는 되돌릴 수 없고, 맵 화면에서는 사용할 수 없습니다*
- 전투당 최대 **30개**의 스냅샷 유지

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

## 설치

1. 최신 릴리스에서 `Sts2UndoMod.dll`과 `Sts2UndoMod.json`을 받습니다.
2. 두 파일을 다음 경로에 복사합니다:
   ```
   <Slay the Spire 2 설치 폴더>/mods/Sts2UndoMod/
   ```
3. 게임을 실행합니다.

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

## 라이선스

[MIT](LICENSE).
