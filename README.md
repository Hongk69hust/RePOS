# RePOS
 RePOS stands for Remote PC ON/OFF and SCADA REALTIME, this is my first serious project in university. It is about turning on\off pc by wireless button, button in the case pc, monitoring power, temperature, voltage, fan speed, calculating real power and warning.
 The concept: I wanted to remotely toggle my PC's power, but I wasn't satisfied with simply using an RF transmitter for that purpose. Instead, I wanted to build a real-time monitoring and alert system using spare microcontrollers—which is why I chose the ESP8266 over the ESP32 or other more powerful, energy-efficient options. While the data collected by the LHM couldn't measure actual power consumption directly, I calculated it using the load-efficiency charts for the power supply provided by f14.
 This project is a solo project, author: Chu Xuân Hồng
Data Collector is files that will collect data from LibreHardwareMonitor and send it to web ip of MCU (http)
We need {
 PAWN.IO
 LibreHardwareMonitor
 Adruino IDE
 VScode or Notepad
.NET 10.4
 }
For this project.
My MCU_BOX stl and step have some bugs, i will fix it soon, you can also do your own boxes that will be fit in your pc case.
*This is just a small SCADA system, not an industrial SCADA system.
