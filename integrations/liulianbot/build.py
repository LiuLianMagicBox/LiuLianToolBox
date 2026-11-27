#!/usr/bin/env python3
"""Build the ToolBox extension with the committed sources of an existing LiuLianBot checkout."""
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import zipfile
import tempfile

HERE = Path(__file__).resolve().parent
PROJECT = HERE.parent.parent


def replace_once(path, anchor, replacement):
    source = path.read_text()
    if source.count(anchor) != 1:
        raise SystemExit(f"Unsupported LiuLianBot version: missing or ambiguous hook in {path.name}: {anchor}")
    path.write_text(source.replace(anchor, replacement, 1))


def prepare(checkout, output):
    git = ["git", "-C", str(checkout)]
    commit = subprocess.check_output([*git, "rev-parse", "--verify", "HEAD^{commit}"], text=True).strip()
    print(f"Building LiuLianBot {commit}", flush=True)
    with tempfile.TemporaryFile() as stream:
        subprocess.run([*git, "archive", "--format=zip", commit, "go.mod", "go.sum", "discord-part", "shared"], stdout=stream, check=True)
        stream.seek(0)
        with zipfile.ZipFile(stream) as archive:
            archive.extractall(output)
    pkg = output / "discord-part/internal/bot"
    for source in HERE.glob("toolbox*.go"):
        shutil.copyfile(source, pkg / source.name)
    locales = pkg / "toolbox_i18n"
    locales.mkdir()
    for source in (PROJECT / "src/ToolBox.Core/Locales").glob("*.json"):
        shutil.copyfile(source, locales / source.name)
    bot = pkg / "bot.go"
    replace_once(bot, "type Bot struct {", "type Bot struct {\n\ttoolbox *tbBridge")
    replace_once(bot, "s.AddHandler(b.gateway)", """s.AddHandler(b.gateway)
	if err := b.initToolbox(); err != nil { cancel(); return nil, err }""")
    replace_once(bot, "if err := b.S.Open(); err != nil {", """if b.toolbox != nil {
		if err := b.toolbox.start(b); err != nil { b.shutdown(); return false, err }
	}
	if err := b.S.Open(); err != nil {""")
    replace_once(bot, '_, err = b.S.ApplicationCommandBulkOverwrite(app, "", b.cat.commands(c.DefaultLanguage),',
                 '''commands := b.cat.commands(c.DefaultLanguage)
			if b.toolbox != nil { commands = append(commands, tbCommand()) }
			_, err = b.S.ApplicationCommandBulkOverwrite(app, "", commands,''')
    signature = "func (b *Bot) interaction(i *dg.Interaction) {"
    replace_once(pkg / "commands.go", signature, signature + """
	if b.toolbox != nil && i.Type == dg.InteractionApplicationCommand && i.ApplicationCommandData().Name == "toolbox" {
		b.toolboxInteraction(i)
		return
	}""")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("checkout", type=Path)
    parser.add_argument("-o", "--output", type=Path, default=PROJECT / "artifacts/liulianbot-toolbox")
    parser.add_argument("--test", action="store_true", help="run ToolBox integration tests with the race detector")
    parser.add_argument("--dotnet", help="also exercise two .NET clients through the live test server")
    args = parser.parse_args()
    if args.dotnet and not args.test:
        parser.error("--dotnet needs --test")
    with tempfile.TemporaryDirectory(prefix="liulianbot-toolbox-") as tmp:
        tree = Path(tmp)
        prepare(args.checkout.resolve(), tree)
        if args.test:
            env = os.environ.copy()
            if args.dotnet:
                env["TOOLBOX_TEST_DOTNET"] = str(Path(shutil.which(args.dotnet) or args.dotnet).resolve())
                env["TOOLBOX_TEST_PROJECT"] = str(PROJECT / "tests/ToolBox.Tests/ToolBox.Tests.csproj")
            subprocess.run(["go", "test", "-race", "-count=1", "-v", "-run", "^TestToolbox",
                            "./discord-part/internal/bot"], cwd=tree, env=env, check=True)
        binary = tree / "liulianbot-toolbox"
        subprocess.run(["go", "build", "-trimpath", "-o", str(binary), "./discord-part/cmd/liulianbot"],
                       cwd=tree, check=True)
        subprocess.run([str(binary), "version"], cwd=tree, check=True)
        target = args.output.resolve()
        target.parent.mkdir(parents=True, exist_ok=True)
        with tempfile.NamedTemporaryFile(dir=target.parent, delete=False) as f:
            staged = Path(f.name)
        try:
            shutil.copy2(binary, staged)
            staged.replace(target)
        finally:
            staged.unlink(missing_ok=True)
        print(target)


if __name__ == "__main__":
    main()
