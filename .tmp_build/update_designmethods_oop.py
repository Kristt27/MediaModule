from __future__ import annotations

import copy
import os
import shutil
import tempfile
import zipfile
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable
from xml.etree import ElementTree as ET

from PIL import Image, ImageDraw, ImageFont


W_NS = "http://schemas.openxmlformats.org/wordprocessingml/2006/main"
ET.register_namespace("w", W_NS)


@dataclass
class UmlClass:
    name: str
    stereotype: str
    attributes: list[str]
    methods: list[str]
    x: int
    y: int
    w: int


def load_font(size: int, bold: bool = False) -> ImageFont.FreeTypeFont | ImageFont.ImageFont:
    candidates = [
        Path(r"C:\Windows\Fonts\segoeui.ttf"),
        Path(r"C:\Windows\Fonts\arial.ttf"),
    ]
    if bold:
        candidates = [
            Path(r"C:\Windows\Fonts\segoeuib.ttf"),
            Path(r"C:\Windows\Fonts\arialbd.ttf"),
        ] + candidates

    for candidate in candidates:
        if candidate.exists():
            return ImageFont.truetype(str(candidate), size=size)
    return ImageFont.load_default()


FONT_TITLE = load_font(28, bold=True)
FONT_STEREO = load_font(18)
FONT_TEXT = load_font(20)
FONT_SMALL = load_font(18)


def line_height(font: ImageFont.ImageFont) -> int:
    bbox = font.getbbox("Ag")
    return bbox[3] - bbox[1] + 6


def draw_box(draw: ImageDraw.ImageDraw, cls: UmlClass) -> tuple[int, int, int, int]:
    stereo_h = line_height(FONT_STEREO)
    title_h = line_height(FONT_TITLE)
    text_h = line_height(FONT_TEXT)
    padding = 14
    attr_lines = max(1, len(cls.attributes))
    method_lines = max(1, len(cls.methods))
    h = padding * 2 + stereo_h + title_h + text_h * (attr_lines + method_lines)

    x1, y1, x2, y2 = cls.x, cls.y, cls.x + cls.w, cls.y + h
    draw.rounded_rectangle((x1, y1, x2, y2), radius=12, outline="#444444", width=2, fill="#FFFFFF")

    header_bottom = y1 + padding + stereo_h + title_h + 8
    attr_bottom = header_bottom + 8 + text_h * attr_lines

    draw.line((x1, header_bottom, x2, header_bottom), fill="#666666", width=2)
    draw.line((x1, attr_bottom, x2, attr_bottom), fill="#666666", width=2)

    cx = x1 + 26
    cy = y1 + padding + stereo_h + title_h // 2
    draw.ellipse((cx - 13, cy - 13, cx + 13, cy + 13), outline="#3A7D44", fill="#D8F3DC", width=2)
    draw.text((cx - 7, cy - 11), "C", font=FONT_SMALL, fill="#1B4332")

    draw.text((x1 + 18, y1 + padding), f"<<{cls.stereotype}>>", font=FONT_STEREO, fill="#6B7280")
    draw.text((x1 + 52, y1 + padding + stereo_h - 2), cls.name, font=FONT_TITLE, fill="#111111")

    text_y = header_bottom + 10
    for attr in cls.attributes:
        draw.text((x1 + 16, text_y), attr, font=FONT_TEXT, fill="#111111")
        text_y += text_h

    text_y = attr_bottom + 10
    for method in cls.methods:
        draw.ellipse((x1 + 18, text_y + 9, x1 + 26, text_y + 17), fill="#2F9E44")
        draw.text((x1 + 34, text_y), method, font=FONT_TEXT, fill="#111111")
        text_y += text_h

    return (x1, y1, x2, y2)


def mid_top(box: tuple[int, int, int, int]) -> tuple[int, int]:
    return ((box[0] + box[2]) // 2, box[1])


def mid_bottom(box: tuple[int, int, int, int]) -> tuple[int, int]:
    return ((box[0] + box[2]) // 2, box[3])


def mid_left(box: tuple[int, int, int, int]) -> tuple[int, int]:
    return (box[0], (box[1] + box[3]) // 2)


def mid_right(box: tuple[int, int, int, int]) -> tuple[int, int]:
    return (box[2], (box[1] + box[3]) // 2)


def draw_arrow(draw: ImageDraw.ImageDraw, start: tuple[int, int], end: tuple[int, int], color: str = "#444444") -> None:
    draw.line((start, end), fill=color, width=2)
    dx = end[0] - start[0]
    dy = end[1] - start[1]
    length = max((dx * dx + dy * dy) ** 0.5, 1)
    ux = dx / length
    uy = dy / length
    px = -uy
    py = ux
    size = 12
    p1 = (end[0] - ux * size + px * size * 0.5, end[1] - uy * size + py * size * 0.5)
    p2 = (end[0] - ux * size - px * size * 0.5, end[1] - uy * size - py * size * 0.5)
    draw.polygon([end, p1, p2], fill=color)


def draw_composition(draw: ImageDraw.ImageDraw, start: tuple[int, int], end: tuple[int, int], color: str = "#444444") -> None:
    draw.line((start, end), fill=color, width=2)
    size = 12
    dx = end[0] - start[0]
    dy = end[1] - start[1]
    length = max((dx * dx + dy * dy) ** 0.5, 1)
    ux = dx / length
    uy = dy / length
    px = -uy
    py = ux
    c1 = (start[0] + ux * size, start[1] + uy * size)
    c2 = (c1[0] + px * size * 0.7, c1[1] + py * size * 0.7)
    c3 = (start[0] + ux * size * 2, start[1] + uy * size * 2)
    c4 = (c1[0] - px * size * 0.7, c1[1] - py * size * 0.7)
    draw.polygon([start, c2, c3, c4], outline=color, fill="#FFFFFF")


def draw_label(draw: ImageDraw.ImageDraw, xy: tuple[int, int], text: str) -> None:
    draw.text(xy, text, font=FONT_SMALL, fill="#4B5563")


def generate_class_diagram(output_path: Path) -> None:
    classes = [
        UmlClass(
            "SaveWindow",
            "boundary",
            [],
            [
                "submitSaveRequest(file)",
                "displayTagSuggestions(tags)",
                "showErrorNotification()",
            ],
            70,
            70,
            350,
        ),
        UmlClass(
            "SettingsWindow",
            "boundary",
            [],
            [
                "openSettings()",
                "editTemplate(template)",
                "showErrorMessage()",
                "updateView()",
            ],
            470,
            70,
            350,
        ),
        UmlClass(
            "FileController",
            "control",
            [],
            [
                "processSave(file)",
                "readFileMetadata(path)",
                "requestTags(file)",
                "saveWithTags(file, tags)",
                "saveWithoutTags(file)",
                "saveWithConflict(file)",
            ],
            900,
            50,
            420,
        ),
        UmlClass(
            "ConfigController",
            "control",
            [],
            [
                "getCurrentSettings()",
                "validateTemplate(template)",
                "saveNamingTemplate(template)",
            ],
            1380,
            70,
            410,
        ),
        UmlClass(
            "EventLogger",
            "service",
            [],
            [
                "logOperation(result)",
                "logError(message)",
            ],
            1870,
            100,
            330,
        ),
        UmlClass(
            "NamingService",
            "service",
            [
                "regexPattern : string",
                "rootDirectory : string",
            ],
            [
                "generateName(order)",
                "validateName(file)",
                "validatePath(path)",
                "checkTemplateValidity(template)",
            ],
            80,
            500,
            420,
        ),
        UmlClass(
            "TagService",
            "service",
            [],
            [
                "preparePrompt(file, order)",
                "requestTags(prompt)",
                "parseTagsList(response)",
            ],
            620,
            500,
            410,
        ),
        UmlClass(
            "DuplicateService",
            "service",
            [],
            [
                "checkFileExisting(file)",
                "checkDuplicate(file)",
                "renameWithIndex(file)",
            ],
            1100,
            500,
            390,
        ),
        UmlClass(
            "FileRepository",
            "service",
            [],
            [
                "save(file)",
                "saveWithConflict(file)",
                "saveWithTags(file, tags)",
                "saveWithoutTags(file)",
                "existsByHash(hash) : bool",
            ],
            1570,
            460,
            430,
        ),
        UmlClass(
            "GraphicFile",
            "entity",
            [
                "fileName : string",
                "filePath : string",
                "hash : string",
                "createdAt : DateTime",
                "tags : List<Tag>",
            ],
            [
                "addTag(tag)",
                "removeTag(tag)",
            ],
            70,
            930,
            350,
        ),
        UmlClass(
            "Tag",
            "entity",
            [
                "key : string",
                "value : string",
            ],
            [],
            500,
            1010,
            260,
        ),
        UmlClass(
            "OrderData",
            "entity",
            [
                "orderId : string",
                "clientName : string",
                "productType : string",
            ],
            [],
            860,
            990,
            300,
        ),
        UmlClass(
            "ElmaClient",
            "gateway",
            [],
            [
                "getOrderData() : OrderData",
            ],
            1230,
            1000,
            320,
        ),
        UmlClass(
            "GigaChatClient",
            "gateway",
            [],
            [
                "requestTags(prompt)",
            ],
            1640,
            1010,
            320,
        ),
        UmlClass(
            "PerceptualHashService",
            "gateway",
            [],
            [
                "generateHash(file)",
            ],
            2010,
            1000,
            390,
        ),
    ]

    image = Image.new("RGB", (2460, 1280), "white")
    draw = ImageDraw.Draw(image)
    boxes = {cls.name: draw_box(draw, cls) for cls in classes}

    draw_arrow(draw, mid_bottom(boxes["SaveWindow"]), mid_top(boxes["FileController"]))
    draw_arrow(draw, mid_bottom(boxes["SettingsWindow"]), mid_top(boxes["ConfigController"]))
    draw_arrow(draw, mid_bottom(boxes["FileController"]), mid_top(boxes["TagService"]))
    draw_arrow(draw, mid_bottom(boxes["FileController"]), mid_top(boxes["DuplicateService"]))
    draw_arrow(draw, mid_bottom(boxes["FileController"]), mid_top(boxes["FileRepository"]))
    draw_arrow(draw, mid_right(boxes["FileController"]), mid_left(boxes["EventLogger"]))
    draw_arrow(draw, mid_left(boxes["FileController"]), mid_right(boxes["NamingService"]))
    draw_arrow(draw, mid_bottom(boxes["ConfigController"]), mid_top(boxes["NamingService"]))
    draw_arrow(draw, mid_right(boxes["ConfigController"]), mid_left(boxes["EventLogger"]))
    draw_arrow(draw, mid_bottom(boxes["NamingService"]), mid_top(boxes["OrderData"]))
    draw_arrow(draw, mid_right(boxes["NamingService"]), mid_left(boxes["ElmaClient"]))
    draw_arrow(draw, mid_bottom(boxes["TagService"]), mid_top(boxes["OrderData"]))
    draw_arrow(draw, mid_bottom(boxes["TagService"]), mid_top(boxes["GigaChatClient"]))
    draw_arrow(draw, mid_bottom(boxes["DuplicateService"]), mid_top(boxes["PerceptualHashService"]))
    draw_arrow(draw, mid_bottom(boxes["FileRepository"]), mid_top(boxes["GraphicFile"]))
    draw_composition(draw, mid_right(boxes["GraphicFile"]), mid_left(boxes["Tag"]))
    draw_label(draw, (470, 1120), "1")
    draw_label(draw, (430, 1120), "0..*")

    output_path.parent.mkdir(parents=True, exist_ok=True)
    image.save(output_path, format="PNG")


def paragraph_text(paragraph: ET.Element) -> str:
    return "".join(node.text or "" for node in paragraph.findall(f".//{{{W_NS}}}t")).strip()


def replace_paragraph_text(paragraph: ET.Element, new_text: str) -> None:
    run_nodes = [child for child in paragraph if child.tag == f"{{{W_NS}}}r"]
    template_run = copy.deepcopy(run_nodes[0]) if run_nodes else ET.Element(f"{{{W_NS}}}r")
    p_pr = paragraph.find(f"{{{W_NS}}}pPr")
    paragraph.clear()
    if p_pr is not None:
        paragraph.append(p_pr)

    new_run = copy.deepcopy(template_run)
    for child in list(new_run):
        if child.tag != f"{{{W_NS}}}rPr":
            new_run.remove(child)
    text_node = ET.Element(f"{{{W_NS}}}t")
    if new_text.startswith(" ") or new_text.endswith(" "):
        text_node.set("{http://www.w3.org/XML/1998/namespace}space", "preserve")
    text_node.text = new_text
    new_run.append(text_node)
    paragraph.append(new_run)


def update_docx(docx_path: Path, class_diagram_bytes: bytes) -> None:
    with zipfile.ZipFile(docx_path, "r") as source_zip:
        document_xml = source_zip.read("word/document.xml")
        root = ET.fromstring(document_xml)

        paragraphs = root.findall(f".//{{{W_NS}}}p")
        text_paragraphs = [(index, p, paragraph_text(p)) for index, p in enumerate(paragraphs)]

        start = None
        for index, paragraph, text in text_paragraphs:
            if text == "Диаграмма классов (class diagram)":
                start = index
                break

        if start is None:
            raise RuntimeError("Не найден раздел диаграммы классов в документе.")

        replacements = [
            "Диаграмма классов показывает структуру системы с учётом сценариев сохранения файла, формирования тегов и управления настройками. На рисунке 12 изображена данная диаграмма.",
            "Рисунок 12 – Class diagram",
            "На диаграмме выделены граничные классы SaveWindow и SettingsWindow, которые отражают действия пользователя из диаграмм последовательности: отправку файла на сохранение, просмотр предложенных тегов, редактирование шаблона и обновление интерфейса.",
            "Управляющие классы FileController и ConfigController координируют выполнение основных сценариев системы и содержат операции processSave, readFileMetadata, getCurrentSettings, validateTemplate и saveNamingTemplate.",
            "Сервисные классы NamingService, TagService и DuplicateService инкапсулируют бизнес-правила: проверку имени и пути, подготовку запроса к GigaChat, разбор списка тегов, поиск дубликатов и переименование конфликтующих файлов.",
            "FileRepository отвечает за варианты сохранения файла методами save, saveWithConflict, saveWithTags и saveWithoutTags, а EventLogger фиксирует результат операций и ошибки через методы logOperation и logError.",
            "Сущности GraphicFile, Tag и OrderData хранят предметные данные. Внешние зависимости ElmaClient, GigaChatClient и PerceptualHashService предоставляют данные заказа, генерацию тегов и вычисление хэша файла.",
            "Структура диаграммы соответствует трёхуровневой архитектуре:",
            "уровень интерфейса пользователя — SaveWindow, SettingsWindow;",
            "уровень управления, бизнес-логики, данных и интеграции — FileController, ConfigController, NamingService, TagService, DuplicateService, FileRepository, EventLogger, GraphicFile, Tag, OrderData, ElmaClient, GigaChatClient, PerceptualHashService.",
        ]

        for offset, new_text in enumerate(replacements, start=1):
            replace_paragraph_text(paragraphs[start + offset], new_text)

        updated_document_xml = ET.tostring(root, encoding="utf-8", xml_declaration=True)

        fd, temp_zip_path_str = tempfile.mkstemp(suffix=".docx")
        os.close(fd)
        temp_zip_path = Path(temp_zip_path_str)
        try:
            with zipfile.ZipFile(temp_zip_path, "w", compression=zipfile.ZIP_DEFLATED) as target_zip:
                for item in source_zip.infolist():
                    if item.filename == "word/document.xml":
                        target_zip.writestr(item, updated_document_xml)
                    elif item.filename == "word/media/image10.png":
                        target_zip.writestr(item, class_diagram_bytes)
                    else:
                        target_zip.writestr(item, source_zip.read(item.filename))
            shutil.move(str(temp_zip_path), str(docx_path))
        finally:
            if temp_zip_path.exists():
                temp_zip_path.unlink()


def require_env(name: str) -> Path:
    value = os.environ.get(name)
    if not value:
        raise RuntimeError(f"Не задана переменная окружения {name}.")
    return Path(value)


def main() -> None:
    docx_path = require_env("DOCX_PATH")
    primary_png = require_env("CLASS_PNG_PRIMARY")
    secondary_png = require_env("CLASS_PNG_SECONDARY")

    generate_class_diagram(primary_png)
    shutil.copy2(primary_png, secondary_png)
    class_diagram_bytes = primary_png.read_bytes()
    update_docx(docx_path, class_diagram_bytes)


if __name__ == "__main__":
    main()
