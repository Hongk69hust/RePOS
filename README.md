# RePOS
- RePOS stands for Remote PC ON/OFF and SCADA REALTIME, this is my first serious project in university. It is about turning on\off pc by wireless button, button in the case pc, monitoring power, temperature, voltage, fan speed, calculating real power and warning.
- ***Please note: This project simulates the use of an MCU to manage and monitor parameters, as well as to calculate non-linear power (based on intrinsic efficiency) under varying load conditions during actual operation. In this instance, I have used my own PC as the subject, but you are free to choose a different one; the core of the project—the code—remains, and you can adapt or fine-tune it as you see fit.
- The concept: I wanted to remotely toggle my PC's power, but I wasn't satisfied with simply using an RF transmitter for that purpose. Instead, I wanted to build a real-time monitoring and alert system using spare microcontrollers—which is why I chose the ESP8266 over the ESP32 or other more powerful, energy-efficient options. While the data collected by the LHM couldn't measure actual power consumption directly, I calculated it using the load-efficiency charts for the power supply provided by f14. I will not talk too much about math in this scope.
- This project is a solo project, author: Chu Xuân Hồng.
- Description:
+ The Sender communicates with the Receiver via the ESP-NOW protocol, transmitting signals to the Receiver’s MAC address. The Sender is configured to use "light sleep" instead of "deep sleep"; this maintains power efficiency while eliminating the need for a manual reset (wake-up) via a second button—a requirement typically associated with deep sleep.
+ The Receiver processes signals from the Sender and triggers a RELAY for 0.6 seconds (simulating the action of pressing a PC power button). It also monitors air pressure and temperature inside the PC case, retrieves data from LibreHardwareMonitor, performs calculations, and sends the information to a web dashboard. Power for this unit is drawn from a USB header rather than a PCIe x1 slot for safety reasons (I have discarded the initial idea of ​​using a PCIe x1 slot). Note that you must enable "standby power" for USB ports within your motherboard's BIOS. For safety, please use a multimeter to verify the positive and negative pins before making any connections.
+ A VBScript file (instead of a batch file) should be used, configured via Task Scheduler to launch automatically with Windows. This ensures the program starts upon boot without opening an intrusive CMD window, while still functioning perfectly in the background.
+ Extreme caution is required when connecting to the POWER SWITCH (PW_SW) headers on the motherboard, even though these pins are not polarity-sensitive. It is recommended to create a Y-splitter cable to connect both the RELAY and the physical case button to the motherboard; this allows for remote power control while ensuring the physical button remains functional as a fallback. You can also control the power via the web dashboard if desired. - Since everyone has different preferences, I highly encourage you to design your own enclosure (though I will still upload mine for your reference). You may also modify the wiring configuration, but please exercise great caution—for instance, in my setup, I’ve included a key-operated rotary switch, which I haven’t shown in the diagram simply because it wasn’t available in the design software I used.
Data Collector is files that will collect data from LibreHardwareMonitor and send it to web ip of MCU (http).
+ One more important thing: You need to let your wifi chanel fixed because it will auto change channel after reboot.
- We need to install{
 PAWN.IO (HAVE TO DO)
 LibreHardwareMonitor 0.9.6 (can be updated in the future so you will need to change C# code. Example: from 0.9.6 to 0.9.7)
 Adruino IDE (HAVE TO DO)
 VScode or Notepad (Optional)
.NET 10.4 (HAVE TO DO)
 }
 For this project.
- My MCU_BOX stl and step have some bugs, i will fix it soon, you can also do your own boxes that will be fit in your pc case.
- This is just a small SCADA system, not an industrial SCADA system.
 Many thanks.
- Future: I want to store datas in 4 weeks and add function to plot charts by minute, hour, day, and week.
        Using MQTT instead of HTTP to reduce load for MCU/ network.
