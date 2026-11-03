"""Generate independent F6 artwork from simple geometry; no upstream logo inputs."""
from pathlib import Path
from PIL import Image, ImageDraw
import shutil

root = Path(__file__).resolve().parents[1]
assets = root / 'src/HoYoShadeHub/Assets'
assets.mkdir(exist_ok=True)
image = Image.new('RGBA', (1024, 1024), (0, 0, 0, 0))
d = ImageDraw.Draw(image)
d.rounded_rectangle((24, 24, 1000, 1000), radius=220, fill='#111b31')
d.rounded_rectangle((74, 74, 950, 950), radius=175, outline='#2e4564', width=10)
# F and 6, drawn on a common grid, with an independent cyan/amber palette.
d.polygon([(230,260),(475,260),(475,348),(322,348),(322,456),(450,456),(450,544),(322,544),(322,754),(230,754)], fill='#66e9d6')
d.rounded_rectangle((538,260,799,754),radius=105, fill='#ffc46c')
d.rectangle((642,348,803,448),fill='#111b31')
d.rounded_rectangle((628,533,709,665),radius=32,fill='#111b31')
d.rounded_rectangle((230,811,799,837),radius=13,fill='#66e9d6')
image.save(assets/'f6-logo.png')
image.save(assets/'f6-logo.ico',sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
for relative in ['src/HoYoShadeHub/logo.ico','src/HoYoShadeHub.Launcher/logo.ico','src/HoYoShadeHub.RPC/logo.ico','src/HoYoShadeHub.Setup/logo.png']:
    target = root/relative
    backup = root/'artifacts/fork-compliance-20261103/before'/relative
    if target.exists() and not backup.exists():
        backup.parent.mkdir(parents=True,exist_ok=True)
        shutil.copyfile(target,backup)
    shutil.copyfile(assets/('f6-logo.png' if target.suffix=='.png' else 'f6-logo.ico'),target)
print('Generated independent F6 artwork.')
