# Photo Organizer

This is a command-line tool to organize your photos into a structured directory hierarchy based on their EXIF metadata. It helps you sort photos by date, camera model, lens, and more, while also detecting and handling duplicate files.

## Features

-   **Custom Directory Structure**: Define your own directory structure using placeholders (e.g., `{YYYY}/{MM}/{Make}_{Model}`).
-   **EXIF-based Organization**: Sorts photos using metadata like date taken, camera make/model, and lens model.
-   **Duplicate Detection**: Identifies duplicate files by calculating their hash values and handles them according to your configuration.
-   **Configurable**: All settings are managed through a simple `config.yml` file.

## Installation

1.  **Clone the repository:**
    ```bash
    git clone <repository_url>
    cd photo-organizer
    ```

2.  **Install dependencies:**
    It is recommended to use a virtual environment.
    ```bash
    python -m venv venv
    source venv/bin/activate  # On Windows, use `venv\Scripts\activate`
    ```
    Then, install the required packages:
    ```bash
    pip install -r requirements.txt
    ```

## Configuration

Before running the application, you need to configure the settings in `photo_organizer/config.yml`.

```yaml
# Source directory where your unorganized photos are located.
source_directory: 'photos/source'

# Destination directory where the organized photos will be saved.
destination_directory: 'photos/organized'

# Directory to move duplicate files to.
duplicate_directory: 'photos/duplicates'

# Pattern to define the new directory structure.
# Available placeholders: {YYYY}, {MM}, {DD}, {Make}, {Model}, {LensModel}
directory_pattern: '{YYYY}/{MM}/{Make}_{Model}'

# Action to take when a duplicate file is found.
# Options: 'move' (move to duplicate_directory), 'delete' (delete permanently), 'skip' (do nothing).
on_duplicate: 'move'
```

## Usage

1.  Place your photos in the `source_directory` specified in your `config.yml`.
2.  Run the main script from the root directory of the project:

    ```bash
    python photo_organizer/main.py
    ```

The script will process the photos and move them to the structured directories inside `destination_directory`.

## Testing

To run the tests for the utility functions, execute the following command from the project root:

```bash
python -m unittest photo_organizer/tests/test_utils.py
```
