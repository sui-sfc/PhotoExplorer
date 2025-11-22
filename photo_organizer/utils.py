import hashlib
import exifread
from datetime import datetime

def get_exif_data(file_path):
    """
    画像ファイルからEXIFデータを読み取る
    """
    with open(file_path, 'rb') as f:
        tags = exifread.process_file(f, details=False)

    exif_data = {}

    # 撮影日時
    if 'EXIF DateTimeOriginal' in tags:
        try:
            exif_data['DateTimeOriginal'] = datetime.strptime(
                str(tags['EXIF DateTimeOriginal']), '%Y:%m:%d %H:%M:%S'
            )
        except ValueError:
            exif_data['DateTimeOriginal'] = None
    else:
        exif_data['DateTimeOriginal'] = None

    # カメラメーカー
    if 'Image Make' in tags:
        exif_data['Make'] = str(tags['Image Make']).strip()
    else:
        exif_data['Make'] = 'Unknown'

    # カメラモデル
    if 'Image Model' in tags:
        exif_data['Model'] = str(tags['Image Model']).strip()
    else:
        exif_data['Model'] = 'Unknown'

    # レンズモデル
    if 'EXIF LensModel' in tags:
        exif_data['LensModel'] = str(tags['EXIF LensModel']).strip()
    else:
        exif_data['LensModel'] = 'Unknown'

    return exif_data

def calculate_hash(file_path, block_size=65536):
    """
    ファイルのハッシュ値(SHA256)を計算する
    """
    sha256 = hashlib.sha256()
    with open(file_path, 'rb') as f:
        for block in iter(lambda: f.read(block_size), b''):
            sha256.update(block)
    return sha256.hexdigest()
