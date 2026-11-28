# 8. 테스트와 디버깅

"빌드는 성공했지만 게임에서는 확인 안 함" 상태로 넘기지 않는 방법을 다룹니다. MC#에는 데이터팩을 Minecraft 없이 실행하는 시뮬레이터가 들어 있습니다.

## 시뮬레이터가 하는 일

| 정확히 계산함 | 기록만 함 (`> 명령`으로 출력) |
|---|---|
| 스코어 (32비트 넘침, 나눗셈 규칙까지), 스토리지, 매크로 | 블록, 좌표 |
| 함수 호출 순서, `/schedule`, `#tick`, `#load`, `/reload` | 소리, 파티클, 효과, 엔티티 NBT |
| `execute if/unless/as/store`, 셀렉터 | |
| 대본 플레이어: 접속, 죽음, 통계, `/trigger`, 우클릭, 인벤토리 | |

즉 **스코어 로직과 실행 순서는 게임 밖에서 확인할 수 있고**, 월드에 대한 명령은 "어떤 명령이 어떤 순서로 나갔는가"까지 확인할 수 있습니다.

## 출력 읽는 법

| 줄 | 뜻 |
|---|---|
| `[t3] 텍스트` | 3틱에 `m.Console.WriteLine` 등으로 모두에게 출력 |
| `[t3] to Steve: 텍스트` | Steve에게만 보낸 메시지 |
| `[t3] [Server] 텍스트` | `say` |
| `[t3] > 명령` | 시뮬레이터가 흉내 내지 않는 명령. 실행은 되었음 |
| `[t3] ! 오류 ← 명령` | Minecraft라도 거부했을 명령 |
| `[t3] * 행동` | 대본의 행동 (`join`, `reload`, …) |

## 대본

`mcs run`에 `--do "틱 행동"`을 붙이거나, 소스 맨 위에 `// @do 틱 행동`을 씁니다. 0틱의 행동은 로드 전에 일어납니다.

| 행동 | 뜻 |
|---|---|
| `reload` | `/reload` (스코어와 스토리지는 유지) |
| `join NAME`, `leave NAME` | 접속, 퇴장 |
| `die NAME`, `respawn NAME` | 죽음, 부활 |
| `kill NAME` | NAME이 무언가를 죽임 |
| `stat NAME 기준 [N]` | 통계 증가 (`minecraft.custom:minecraft.jump` 등) |
| `trigger NAME 목표 [set\|add N]` | `/trigger` |
| `use NAME 아이템`, `click NAME` | 아이템 사용, 들고 있는 아이템으로 우클릭 |
| `hold NAME 슬롯`, `offhand NAME 슬롯` | 핫바 선택, 왼손으로 옮기기 |

그 밖의 옵션: `--ticks N` (실행할 틱 수), `--format N` (특정 팩 포맷의 층으로 실행).

## 로직을 검사하는 프로그램

계산 로직은 작은 검사 프로그램으로 확인할 수 있습니다.

[`examples/t08_testing.mcs`](examples/t08_testing.mcs):

```csharp
[Inline] static void Expect(string name, int actual, int expected)
{
    if (actual == expected) m.Console.WriteLine($"ok   {name}");
    else m.Console.WriteLine($"FAIL {name}: {actual} (기대값 {expected})");
}

static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

public static void Main()
{
    Expect("가운데", Clamp(5, 0, 10), 5);
    Expect("아래", Clamp(-3, 0, 10), 0);
    Expect("위", Clamp(99, 0, 10), 10);
    Expect("나눗셈", -7 / 2, -3);
}
```

```
[t0] ok   가운데
[t0] ok   아래
[t0] ok   위
[t0] ok   나눗셈
```

검사할 로직을 `Main()`이 없는 파일(예: `src/Combat.mcs`)에 두면, 검사 프로그램과 함께 따로 빌드할 수 있습니다. 검사 프로그램은 본 프로젝트의 `mcs.json`에 넣지 않습니다.

```sh
mcs run src/Combat.mcs tests/CombatTest.mcs -n combat_test -o out/combat_test
```

## 회귀 테스트로 만들기

기대 출력을 파일로 저장해 두면, 코드를 고칠 때마다 결과가 바뀌었는지 바로 알 수 있습니다.

```sh
mcs run game.mcs --ticks 40 --do "0 join Steve" > expected.txt    # 처음 한 번, 내용 확인 후 저장
mcs run game.mcs --ticks 40 --do "0 join Steve" | diff expected.txt -   # 이후 매번
```

MC# 저장소 자체도 이 방식으로 테스트합니다 (`tests/run.py`, `tests/cases/*.mcs` + `.out`). 이 튜토리얼의 예제도 여기에 포함되어 있습니다.

## 디버깅 체크리스트

결과가 예상과 다를 때:

1. **`mcs run`으로 재현되나?** 재현되면 게임 밖에서 고칠 수 있습니다. 재현되지 않으면 블록, 위치, 엔티티 NBT 쪽 문제입니다.
2. **`!` 줄이 있나?** Minecraft가 거부한 명령입니다. 대부분 잘못된 ID나 아직 없는 스코어입니다.
3. **실행 맥락이 맞나?** 이벤트 밖이나 `Wait()` 뒤에서 `@s`를 쓰지 않았는지 봅니다 ([6장](06-context.md)).
4. **정적 필드 초기값을 바꿨나?** 기존 월드에는 반영되지 않습니다 ([2장](02-variables.md)).
5. **게임 안에서 값을 보려면:** `/scoreboard players get $static.<경로>.<필드> mcs.<ns>`

## 정리

- `mcs run`으로 스코어 로직과 순서를 게임 밖에서 확인합니다.
- 대본(`--do`)으로 플레이어 행동과 `/reload`를 재현합니다.
- 기대 출력을 저장해 회귀 테스트로 씁니다.
- 관련 명세: [부속서 C 시뮬레이터](../spec.md#부속서-c-참고-시뮬레이터로-확인할-수-있는-범위), [부속서 E 규칙과 테스트](../spec.md#부속서-e-참고-규칙과-테스트의-대응)

[튜토리얼 처음으로](README.md)
