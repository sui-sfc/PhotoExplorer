import unittest
import os
from unittest.mock import patch, mock_open
from datetime import datetime

from photo_organizer.utils import calculate_hash, get_exif_data

class TestUtils(unittest.TestCase):

    def setUp(self):
        """テスト用のダミーファイルを作成する"""
        self.test_file_content = "this is a test file for hashing"
        self.test_file_path = "test_hash_file.txt"
        with open(self.test_file_path, "w") as f:
            f.write(self.test_file_content)

    def tearDown(self):
        """テスト用のダミーファイルを削除する"""
        if os.path.exists(self.test_file_path):
            os.remove(self.test_file_path)

    def test_calculate_hash(self):
        """calculate_hash関数が正しいSHA256ハッシュを計算するかをテストする"""
        expected_hash = "ceaae15918e3236851a8b85efa977eda4e5bae5fa9d09ee48ec973993f229d1c"
        actual_hash = calculate_hash(self.test_file_path)
        self.assertEqual(actual_hash, expected_hash)

    @patch('photo_organizer.utils.exifread.process_file')
    def test_get_exif_data_with_valid_data(self, mock_process_file):
        """get_exif_dataが有効なEXIFデータを正しく解析するかをテストする"""
        # exifread.process_fileの戻り値をモックする
        mock_tags = {
            'EXIF DateTimeOriginal': '2023:11:21 10:30:00',
            'Image Make': 'TestMake',
            'Image Model': 'TestModel',
            'EXIF LensModel': 'TestLens'
        }
        mock_process_file.return_value = mock_tags

        # ファイルI/Oをモックしてget_exif_dataを呼び出す
        with patch('builtins.open', mock_open(read_data=b"dummy data")):
            exif_data = get_exif_data("dummy_path")

        # 結果を検証
        self.assertEqual(exif_data['DateTimeOriginal'], datetime(2023, 11, 21, 10, 30, 0))
        self.assertEqual(exif_data['Make'], 'TestMake')
        self.assertEqual(exif_data['Model'], 'TestModel')
        self.assertEqual(exif_data['LensModel'], 'TestLens')

    @patch('photo_organizer.utils.exifread.process_file')
    def test_get_exif_data_with_missing_data(self, mock_process_file):
        """get_exif_dataが存在しないEXIFデータに対してデフォルト値を返すかをテストする"""
        # 空の辞書を返すようにモックする
        mock_process_file.return_value = {}

        with patch('builtins.open', mock_open(read_data=b"dummy data")):
            exif_data = get_exif_data("dummy_path")

        # 結果を検証
        self.assertIsNone(exif_data['DateTimeOriginal'])
        self.assertEqual(exif_data['Make'], 'Unknown')
        self.assertEqual(exif_data['Model'], 'Unknown')
        self.assertEqual(exif_data['LensModel'], 'Unknown')

if __name__ == '__main__':
    # -p 'test_*.py' をつけて実行することで、プロジェクトルートからのテスト実行をシミュレート
    unittest.main(argv=['first-arg-is-ignored'], exit=False)
