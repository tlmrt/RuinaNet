# RuinaNet v1.0 — experimental two-player co-op

[中文](README.md) | English | [한국어](README.ko.md) | [日本語](README.ja.md)

**Made entirely by AI, with zero human-made content.**

Source: https://github.com/tlmrt/RuinaNet — MIT license.
Co-op matchmaking and bug reports: QQ group **1130976144**. You can also use [GitHub Issues](https://github.com/tlmrt/RuinaNet/issues).

This is an unofficial Library of Ruina mod. The translated documents explain how to use it; the mod interface is still mainly Chinese.

## Status

v1.0 uses protocol **R48**. Both players must use the same version and compatible page/mod resources. Do not mix it with the older v3.17.1/R47 build.

Steam friend lobbies and relay transport are experimental. Compilation and 712 automated checks passed, but real networking with two PCs and two Steam accounts has not been fully tested. The controlled Steam API tests do not prove relay availability for this game's App ID or every network. Testers and reproducible reports are welcome.

## Start playing

1. Both players launch the game with mods enabled and enable RuinaNet.
2. Open the Invitation screen, then click “联机接待” at the upper left.
3. **Steam:** the host clicks “创建 Steam 房间”, then “邀请 Steam 好友”. While the guest's game is running, accept the invitation. The room number is filled into the mod panel; click “加入 Steam”. Manually entering the host's displayed room number also works.
4. **UDP:** the host clicks “创建接待室”; the guest enters a reachable `IP:port` or `hostname:port` and clicks “加入接待”. Default: UDP 27015. Internet connections need a working UDP tunnel, virtual LAN, or port forwarding.
5. Wait for the connected status before starting a reception. Accept the same reception, assign librarians, finish equipment setup, and ready up. The host starts the battle. Both players confirm their card assignments each Scene.
6. Ordinary Abnormality first attempts and rematches use a separate co-op preparation window. The host starts from the game's native challenge/rematch menu; the guest accepts. The usual Battle Preparation screen is not required.

## What is coordinated

Librarian assignments, Key Pages, Combat Page decks, attributed passives and appearance; hands, Speed Dice, page assignments, targets, clashes, rolls and battle state; shared E.G.O use. Abnormality/Emotion page selection currently belongs to the host.

## Limits

- Two players only. No mid-battle joining, host migration or battle reconnection. After a disconnect, end that battle and reconnect for a new attempt.
- Patron Librarians belong to the host; Assistant Librarians follow the assignment. In a one-librarian battle, the guest watches and participates in confirmations.
- Floor Realizations and the final Keter battle are not integrated. Special mechanics, phase changes and multi-Act transitions need individual testing.
- An Abnormality first attempt requires matching target-floor levels; rematches require both players to have cleared that challenge.
- No full save transfer or story-progression sharing. Mods that change the same battle flow may conflict.
- Previous reports involved hands, clashes, E.G.O, Key Pages, passives and HP. Fixes exist, but all combinations and receptions have not been verified.

If Steam fails, disconnect and try the existing UDP option. Testing two instances on one Steam account does not replace a two-account Steam test.

## Reports and source builds

Provide both versions, host/guest roles, reception/floor, exact steps, screenshots, other mods and both logs. State Steam/UDP and LAN/Internet/same-PC conditions. Remove personal information from logs; do not send account details or saves.

Build instructions: [docs/BUILD.md](docs/BUILD.md). Game assemblies and native Steam libraries are not included in the source repository.

Not affiliated with Project Moon.
