#pragma once
#include <QString>

inline QString SwitcherStyle() {
    return QStringLiteral(R"(
        QWidget{background:#20252d;color:#edf1f7;font-size:15px;}
        QLabel#title{font-size:26px;font-weight:600;}
        QLabel#reviewTitle{font-size:22px;}
        QLabel#hint{color:#b0bac8;font-size:13px;}
        QLabel#error{color:#e7a6a1;border:1px solid #7c494b;padding:12px;}
        QLabel#warning{color:#e6c387;background:#393022;padding:12px;}
        QLabel#change{border-bottom:1px solid #373f4b;padding:12px;}
        QPushButton,QLineEdit{min-height:36px;padding:4px 12px;border:1px solid #373f4b;border-radius:5px;}
        QPushButton{background:#20252d;color:#edf1f7;}
        QPushButton:hover{background:#293649;color:#edf1f7;border-color:#99c5ff;}
        QPushButton:pressed{background:#15181d;color:#edf1f7;}
        QPushButton:focus,QLineEdit:focus{border:2px solid #99c5ff;}
        QPushButton#primary{background:#99c5ff;color:#17283d;}
        QPushButton#primary:hover{background:#b5d5ff;color:#17283d;}
        QPushButton#primary:pressed{background:#81b5f8;color:#17283d;}
        QPushButton:disabled,QPushButton#primary:disabled{background:#20252d;color:#8190a3;border-color:#373f4b;}
        QLineEdit,QListWidget{background:#15181d;}
        QListWidget{border:0;}
        QListWidget::item{color:#edf1f7;padding:12px;border:1px solid #373f4b;border-radius:6px;margin-bottom:8px;font-size:19px;}
        QListWidget::item:hover{background:#293649;color:#edf1f7;border-color:#99c5ff;}
        QListWidget::item:selected{background:#293649;color:#edf1f7;border:2px solid #99c5ff;}
        QListWidget[controllerSelected="false"]::item:selected{background:#20252d;color:#edf1f7;border:1px solid #373f4b;}
        QListWidget::item:disabled{background:#15181d;color:#8190a3;border-color:#373f4b;}
        QPushButton[controllerSelected="true"],QComboBox[controllerSelected="true"],
        QSpinBox[controllerSelected="true"],QKeySequenceEdit[controllerSelected="true"]{border:2px solid #99c5ff;}
        QScrollArea{border:0;}
    )");
}
