import os
import shutil
import yaml
from utils import get_exif_data, calculate_hash

def load_config(config_path='config.yml'):
    """
    設定ファイルを読み込む
    """
    with open(config_path, 'r', encoding='utf-8') as f:
        return yaml.safe_load(f)

def organize_photos(config):
    """
    設定に基づいて写真を整理する
    """
    source_dir = config['source_directory']
    dest_dir = config['destination_directory']
    duplicate_dir = config['duplicate_directory']
    pattern = config['directory_pattern']
    on_duplicate = config.get('on_duplicate', 'skip')

    # 必要なディレクトリを作成
    os.makedirs(dest_dir, exist_ok=True)
    if on_duplicate == 'move' and not os.path.exists(duplicate_dir):
        os.makedirs(duplicate_dir, exist_ok=True)

    # 処理済みのファイルのハッシュを保存するセット
    processed_hashes = set()

    # 既存ファイルのハッシュをスキャン (オプション)
    print("Scanning destination directory for existing files...")
    for root, _, files in os.walk(dest_dir):
        for name in files:
            path = os.path.join(root, name)
            processed_hashes.add(calculate_hash(path))


    # ソースディレクトリ内のすべてのファイルを処理
    if not os.path.exists(source_dir):
        print(f"Error: Source directory not found at '{source_dir}'")
        return

    for filename in os.listdir(source_dir):
        source_path = os.path.join(source_dir, filename)

        if not os.path.isfile(source_path):
            continue

        print(f"Processing: {filename}")

        file_hash = calculate_hash(source_path)

        if file_hash in processed_hashes:
            print(f"  - Duplicate found: {filename}")
            if on_duplicate == 'move':
                shutil.move(source_path, os.path.join(duplicate_dir, filename))
                print(f"  - Moved to duplicates folder.")
            elif on_duplicate == 'delete':
                os.remove(source_path)
                print(f"  - Deleted.")
            else:
                print(f"  - Skipped.")
            continue

        processed_hashes.add(file_hash)

        exif_data = get_exif_data(source_path)

        if not exif_data or not exif_data.get('DateTimeOriginal'):
            print(f"  - Could not read EXIF DateTime for {filename}. Skipping.")
            continue

        dt = exif_data['DateTimeOriginal']
        replacements = {
            '{YYYY}': f"{dt.year:04d}",
            '{MM}': f"{dt.month:02d}",
            '{DD}': f"{dt.day:02d}",
            '{Make}': exif_data['Make'],
            '{Model}': exif_data['Model'].replace('/', '_'),
            '{LensModel}': exif_data['LensModel'].replace('/', '_'),
        }

        new_dir_name = pattern
        for placeholder, value in replacements.items():
            new_dir_name = new_dir_name.replace(placeholder, str(value))

        final_dest_dir = os.path.join(dest_dir, new_dir_name)
        os.makedirs(final_dest_dir, exist_ok=True)

        destination_path = os.path.join(final_dest_dir, filename)
        shutil.move(source_path, destination_path)
        print(f"  - Moved to: {destination_path}")


def main():
    """
    メイン処理
    """
    try:
        config = load_config('photo_organizer/config.yml')
        organize_photos(config)
        print("\nPhoto organization complete!")
    except FileNotFoundError:
        print("Error: 'photo_organizer/config.yml' not found.")
    except Exception as e:
        print(f"An error occurred: {e}")


if __name__ == '__main__':
    main()
